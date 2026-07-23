using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class SessionService : ISessionService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<SessionService> _logger;

    public SessionService(IAMDbContext context, ILogger<SessionService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<UserSession> CreateSessionAsync(Guid userId, string? ipAddress, string? userAgent, TimeSpan duration, CancellationToken ct = default)
    {
        var rawToken = GenerateSessionToken();
        var tokenHash = HashToken(rawToken);

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SessionToken = tokenHash,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            DeviceInfo = ParseDeviceInfo(userAgent),
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(duration)
        };

        _context.UserSessions.Add(session);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Session created for user {UserId} from {IpAddress} ({DeviceInfo})",
            userId, ipAddress, session.DeviceInfo);

        // Return the raw token on the entity so the caller can transmit it.
        // The persisted row stores the hash only.
        session.SessionToken = rawToken;
        return session;
    }

    public async Task<List<UserSession>> GetActiveSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _context.UserSessions
            .Where(s => s.UserId == userId && !s.IsRevoked && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastActivityAt)
            .ToListAsync(ct);
    }

    public async Task<bool> RevokeSessionAsync(Guid sessionId, Guid userId, string reason = "user_action", CancellationToken ct = default)
    {
        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && !s.IsRevoked, ct);

        if (session == null)
        {
            return false;
        }

        session.IsRevoked = true;
        session.RevokedAt = DateTime.UtcNow;
        session.RevokedReason = reason;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Session {SessionId} revoked for user {UserId}, reason: {Reason}",
            sessionId, userId, reason);

        return true;
    }

    public async Task<int> RevokeAllOtherSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var sessions = await _context.UserSessions
            .Where(s => s.UserId == userId && s.Id != currentSessionId && !s.IsRevoked && s.ExpiresAt > now)
            .ToListAsync(ct);

        foreach (var session in sessions)
        {
            session.IsRevoked = true;
            session.RevokedAt = DateTime.UtcNow;
            session.RevokedReason = "user_action";
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Revoked {Count} other sessions for user {UserId} (kept {CurrentSessionId})",
            sessions.Count, userId, currentSessionId);

        return sessions.Count;
    }

    public async Task<int> RevokeAllUserSessionsAsync(Guid userId, string reason = "admin_action", CancellationToken ct = default)
    {
        var sessions = await _context.UserSessions
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .ToListAsync(ct);

        foreach (var session in sessions)
        {
            session.IsRevoked = true;
            session.RevokedAt = DateTime.UtcNow;
            session.RevokedReason = reason;
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Revoked all {Count} sessions for user {UserId}, reason: {Reason}",
            sessions.Count, userId, reason);

        return sessions.Count;
    }

    public async Task<bool> ValidateSessionAsync(string sessionToken, CancellationToken ct = default)
    {
        var tokenHash = HashToken(sessionToken);
        var now = DateTime.UtcNow;

        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.SessionToken == tokenHash && !s.IsRevoked && s.ExpiresAt > now, ct);

        return session != null;
    }

    public async Task UpdateActivityAsync(string sessionToken, CancellationToken ct = default)
    {
        var tokenHash = HashToken(sessionToken);

        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.SessionToken == tokenHash && !s.IsRevoked, ct);

        if (session != null)
        {
            session.LastActivityAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task<int> CleanupExpiredSessionsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow;

        // Clean up sessions that are expired or were revoked more than 30 days ago
        var expiredSessions = await _context.UserSessions
            .Where(s => s.ExpiresAt < cutoff || (s.IsRevoked && s.RevokedAt < cutoff.AddDays(-30)))
            .ToListAsync(ct);

        if (expiredSessions.Count > 0)
        {
            _context.UserSessions.RemoveRange(expiredSessions);
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Cleaned up {Count} expired/revoked sessions", expiredSessions.Count);
        }

        return expiredSessions.Count;
    }

    public async Task<SessionStatistics> GetStatisticsAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var todayStart = now.Date;

        // Base query: active sessions (optionally filtered by tenant through user roles)
        IQueryable<UserSession> baseQuery = _context.UserSessions
            .Where(s => !s.IsRevoked && s.ExpiresAt > now);

        if (tenantId.HasValue)
        {
            // Filter to users who have a role in the given tenant
            var tenantUserIds = _context.UserRoles
                .Where(ur => ur.TenantId == tenantId.Value)
                .Select(ur => ur.UserId)
                .Distinct();

            baseQuery = baseQuery.Where(s => tenantUserIds.Contains(s.UserId));
        }

        var activeSessions = await baseQuery.ToListAsync(ct);

        var stats = new SessionStatistics
        {
            TotalActiveSessions = activeSessions.Count,
            UniqueUsers = activeSessions.Select(s => s.UserId).Distinct().Count(),
            SessionsByDevice = activeSessions
                .GroupBy(s => s.DeviceInfo ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            SessionsCreatedToday = await _context.UserSessions
                .Where(s => s.CreatedAt >= todayStart)
                .CountAsync(ct),
            SessionsRevokedToday = await _context.UserSessions
                .Where(s => s.IsRevoked && s.RevokedAt >= todayStart)
                .CountAsync(ct)
        };

        return stats;
    }

    // --- Private helpers ---

    /// <summary>
    /// Generate a cryptographically random 32-byte token encoded as Base64.
    /// </summary>
    private static string GenerateSessionToken()
    {
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    /// <summary>
    /// SHA256 hash of the raw token for secure storage/lookup.
    /// </summary>
    private static string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Parse a User-Agent string into a human-readable device description.
    /// Examples: "Chrome on Windows", "Safari on macOS", "Firefox on Linux", "Mobile Safari on iOS"
    /// </summary>
    internal static string? ParseDeviceInfo(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;

        var browser = ParseBrowser(userAgent);
        var os = ParseOperatingSystem(userAgent);

        if (browser == null && os == null)
            return "Unknown device";

        if (browser != null && os != null)
            return $"{browser} on {os}";

        return browser ?? os ?? "Unknown device";
    }

    private static string? ParseBrowser(string userAgent)
    {
        // Order matters: more specific patterns first
        if (Regex.IsMatch(userAgent, @"Edg[e/]", RegexOptions.IgnoreCase))
            return "Edge";
        if (Regex.IsMatch(userAgent, @"OPR/|Opera", RegexOptions.IgnoreCase))
            return "Opera";
        if (Regex.IsMatch(userAgent, @"Chrome/", RegexOptions.IgnoreCase) && !Regex.IsMatch(userAgent, @"Chromium", RegexOptions.IgnoreCase))
            return "Chrome";
        if (Regex.IsMatch(userAgent, @"Chromium", RegexOptions.IgnoreCase))
            return "Chromium";
        if (Regex.IsMatch(userAgent, @"Safari/", RegexOptions.IgnoreCase) && !Regex.IsMatch(userAgent, @"Chrome/", RegexOptions.IgnoreCase))
            return Regex.IsMatch(userAgent, @"Mobile", RegexOptions.IgnoreCase) ? "Mobile Safari" : "Safari";
        if (Regex.IsMatch(userAgent, @"Firefox/", RegexOptions.IgnoreCase))
            return "Firefox";
        if (Regex.IsMatch(userAgent, @"MSIE|Trident", RegexOptions.IgnoreCase))
            return "Internet Explorer";

        return null;
    }

    private static string? ParseOperatingSystem(string userAgent)
    {
        if (Regex.IsMatch(userAgent, @"Windows NT", RegexOptions.IgnoreCase))
            return "Windows";
        if (Regex.IsMatch(userAgent, @"Mac OS X", RegexOptions.IgnoreCase))
            return Regex.IsMatch(userAgent, @"iPhone|iPad|iPod", RegexOptions.IgnoreCase) ? "iOS" : "macOS";
        if (Regex.IsMatch(userAgent, @"Android", RegexOptions.IgnoreCase))
            return "Android";
        if (Regex.IsMatch(userAgent, @"Linux", RegexOptions.IgnoreCase))
            return "Linux";
        if (Regex.IsMatch(userAgent, @"CrOS", RegexOptions.IgnoreCase))
            return "Chrome OS";

        return null;
    }
}
