using System.Security.Cryptography;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Sign in on a new browser by approving on a device that is already signed in (QR code, push, popup).
/// Safety rules: the browser that started the request holds a secret and is the only one that can finish it; the
/// approver must choose the number shown on that browser (number matching, 3 wrong = denied); a request lives 3
/// minutes and works once; starting requests is rate limited per IP; every step is written to the audit log.
/// </summary>
public class LoginApprovalService : ILoginApprovalService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);
    public const int MaxStartsPerIpPer10Minutes = 10;
    public const int MaxFailedMatches = 3;
    private const int MaxPendingPerUserForPush = 3;

    private readonly IAMDbContext _context;
    private readonly ILoginApprovalNotifier _notifier;
    private readonly ILogger<LoginApprovalService> _logger;

    public LoginApprovalService(IAMDbContext context, ILoginApprovalNotifier notifier, ILogger<LoginApprovalService> logger)
    {
        _context = context;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<LoginApprovalStart?> StartAsync(string? email, bool rememberMe, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(ipAddress))
        {
            var since = now.AddMinutes(-10);
            var recent = await _context.LoginApprovals.CountAsync(a => a.RequestIp == ipAddress && a.CreatedAt > since, ct);
            if (recent >= MaxStartsPerIpPer10Minutes)
            {
                return null;
            }
        }

        User? user = null;
        var normalized = email?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(normalized))
        {
            user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized && u.IsActive, ct);
        }

        var secret = NewSecret();
        var approval = new LoginApproval
        {
            PollSecretHash = Hash(secret),
            UserId = user?.Id,
            Status = LoginApprovalStatus.Pending,
            MatchNumber = RandomNumberGenerator.GetInt32(10, 100),
            RememberMe = rememberMe,
            RequestIp = Truncate(ipAddress, 50),
            RequestUserAgent = Truncate(userAgent, 500),
            RequestDevice = SessionService.ParseDeviceInfo(userAgent) ?? "Unknown device",
            CreatedAt = now,
            ExpiresAt = now.Add(Lifetime)
        };
        _context.LoginApprovals.Add(approval);
        AddAudit(user?.Id, "LoginApprovalRequested", approval, ipAddress, userAgent, new { named = user != null });
        await _context.SaveChangesAsync(ct);

        if (user != null)
        {
            var pending = await _context.LoginApprovals.CountAsync(
                a => a.UserId == user.Id && a.Status == LoginApprovalStatus.Pending && a.ExpiresAt > now, ct);
            if (pending <= MaxPendingPerUserForPush)
            {
                await _notifier.NotifyAsync(user.Id, approval, ct);
            }
        }

        return new LoginApprovalStart(approval.Id, secret, approval.MatchNumber, approval.ExpiresAt);
    }

    public async Task<LoginApprovalStatus?> GetStatusAsync(Guid id, string pollSecret, CancellationToken ct = default)
    {
        var approval = await _context.LoginApprovals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval == null || !SecretMatches(approval, pollSecret))
        {
            return null;
        }

        return EffectiveStatus(approval);
    }

    public async Task<ConsumeResult> ConsumeAsync(Guid id, string pollSecret, CancellationToken ct = default)
    {
        var approval = await _context.LoginApprovals.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval == null || !SecretMatches(approval, pollSecret))
        {
            return new ConsumeResult(false, null, false, "Invalid sign-in request", LoginApprovalStatus.Expired);
        }

        var status = EffectiveStatus(approval);
        if (status != LoginApprovalStatus.Approved || approval.UserId == null)
        {
            return new ConsumeResult(false, null, false, status == LoginApprovalStatus.Pending ? "Not approved yet" : "Sign-in request is no longer valid", status);
        }

        approval.Status = LoginApprovalStatus.Consumed;
        approval.ConsumedAt = DateTime.UtcNow;
        approval.Version++;
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else finished it first: a request signs in once.
            return new ConsumeResult(false, null, false, "Sign-in request is no longer valid", LoginApprovalStatus.Consumed);
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == approval.UserId, ct);
        if (user == null || !user.IsActive)
        {
            return new ConsumeResult(false, null, false, "Account is inactive", LoginApprovalStatus.Consumed);
        }

        return new ConsumeResult(true, user, approval.RememberMe, null, LoginApprovalStatus.Consumed);
    }

    public async Task<IReadOnlyList<LoginApprovalInfo>> ListPendingAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await _context.LoginApprovals.AsNoTracking()
            .Where(a => a.UserId == userId && a.Status == LoginApprovalStatus.Pending && a.ExpiresAt > now)
            .OrderByDescending(a => a.CreatedAt)
            .Take(5)
            .ToListAsync(ct);
        return rows.Select(a => ToInfo(a, namedYou: true)).ToList();
    }

    public async Task<LoginApprovalInfo?> GetForApproverAsync(Guid id, Guid approverUserId, CancellationToken ct = default)
    {
        var approval = await _context.LoginApprovals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval == null || EffectiveStatus(approval) != LoginApprovalStatus.Pending)
        {
            return null;
        }

        if (approval.UserId != null && approval.UserId != approverUserId)
        {
            return null;
        }

        return ToInfo(approval, namedYou: approval.UserId == approverUserId);
    }

    public async Task<ApproveOutcome> ApproveAsync(Guid id, Guid approverUserId, Guid? approverRefreshTokenId, int chosenNumber, CancellationToken ct = default)
    {
        var approval = await _context.LoginApprovals.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval == null)
        {
            return ApproveOutcome.NotFound;
        }

        if (EffectiveStatus(approval) != LoginApprovalStatus.Pending)
        {
            return ApproveOutcome.NotPending;
        }

        if (approval.UserId != null && approval.UserId != approverUserId)
        {
            return ApproveOutcome.WrongUser;
        }

        if (chosenNumber != approval.MatchNumber)
        {
            approval.FailedMatches++;
            approval.Version++;
            var tooMany = approval.FailedMatches >= MaxFailedMatches;
            if (tooMany)
            {
                approval.Status = LoginApprovalStatus.Denied;
                approval.DecidedAt = DateTime.UtcNow;
            }

            AddAudit(approverUserId, tooMany ? "LoginApprovalDeniedTooManyWrong" : "LoginApprovalWrongNumber", approval, null, null, new { approval.FailedMatches });
            await SaveOrConflictAsync(ct);
            return tooMany ? ApproveOutcome.TooManyWrong : ApproveOutcome.WrongNumber;
        }

        approval.UserId = approverUserId;
        approval.Status = LoginApprovalStatus.Approved;
        approval.ApprovedByUserId = approverUserId;
        approval.ApprovedByRefreshTokenId = approverRefreshTokenId;
        approval.DecidedAt = DateTime.UtcNow;
        approval.Version++;
        AddAudit(approverUserId, "LoginApprovalApproved", approval, null, null, new { approverRefreshTokenId, newDevice = approval.RequestDevice, newIp = approval.RequestIp });
        return await SaveOrConflictAsync(ct) ? ApproveOutcome.Approved : ApproveOutcome.NotPending;
    }

    public async Task<bool> DenyAsync(Guid id, Guid approverUserId, CancellationToken ct = default)
    {
        var approval = await _context.LoginApprovals.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval == null || EffectiveStatus(approval) != LoginApprovalStatus.Pending)
        {
            return false;
        }

        if (approval.UserId != null && approval.UserId != approverUserId)
        {
            return false;
        }

        approval.Status = LoginApprovalStatus.Denied;
        approval.DecidedAt = DateTime.UtcNow;
        approval.Version++;
        AddAudit(approverUserId, "LoginApprovalDenied", approval, null, null, new { newDevice = approval.RequestDevice, newIp = approval.RequestIp });
        return await SaveOrConflictAsync(ct);
    }

    // --- helpers ---

    private async Task<bool> SaveOrConflictAsync(CancellationToken ct)
    {
        try
        {
            await _context.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogInformation("Login approval changed concurrently; the other decision wins");
            return false;
        }
    }

    private void AddAudit(Guid? userId, string action, LoginApproval approval, string? ip, string? ua, object details)
    {
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            Resource = "LoginApproval",
            Details = System.Text.Json.JsonSerializer.Serialize(new { approvalId = approval.Id, details }),
            IpAddress = ip ?? approval.RequestIp,
            UserAgent = ua ?? approval.RequestUserAgent
        });
    }

    private static LoginApprovalStatus EffectiveStatus(LoginApproval approval)
        => approval.Status == LoginApprovalStatus.Pending && approval.ExpiresAt <= DateTime.UtcNow
            ? LoginApprovalStatus.Expired
            : approval.Status == LoginApprovalStatus.Approved && approval.ExpiresAt.AddMinutes(2) <= DateTime.UtcNow
                ? LoginApprovalStatus.Expired
                : approval.Status;

    private static LoginApprovalInfo ToInfo(LoginApproval a, bool namedYou)
        => new(a.Id, a.RequestDevice ?? "Unknown device", a.RequestIp, a.CreatedAt, a.ExpiresAt, ChoicesFor(a), namedYou);

    /// <summary>The correct number plus two decoys, in a fixed order per request (so reloading the page reveals nothing).</summary>
    internal static int[] ChoicesFor(LoginApproval a)
    {
        // Stable seed (HashCode.Combine is randomised per process, so it would change between requests).
        var rng = new Random(BitConverter.ToInt32(a.Id.ToByteArray(), 0) ^ (a.MatchNumber * 7919));
        var set = new List<int> { a.MatchNumber };
        while (set.Count < 3)
        {
            var decoy = rng.Next(10, 100);
            if (!set.Contains(decoy))
            {
                set.Add(decoy);
            }
        }

        return set.OrderBy(_ => rng.Next()).ToArray();
    }

    private static bool SecretMatches(LoginApproval approval, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(approval.PollSecretHash);
        var actual = Encoding.ASCII.GetBytes(Hash(secret));
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
