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

    // Task 5568: a session is a real sign-in, i.e. an unrevoked, unexpired refresh token. The session id IS the
    // refresh token id (the signed refresh_token_id claim). The UserSessions table is never written by sign-in,
    // so nothing here reads or writes it except the legacy cleanup below.

    public async Task<List<UserSession>> GetActiveSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tokens = await _context.RefreshTokens.AsNoTracking()
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null && rt.ExpiresAt > now)
            .OrderByDescending(rt => rt.CreatedAt)
            .ToListAsync(ct);

        // A rotation stamps the successor's CreatedAt, so CreatedAt is the last time this sign-in was used.
        return tokens.Select(rt => new UserSession
        {
            Id = rt.Id,
            UserId = rt.UserId,
            IpAddress = rt.IpAddress,
            UserAgent = rt.UserAgent,
            DeviceInfo = ParseDeviceInfo(rt.UserAgent),
            CreatedAt = rt.CreatedAt,
            LastActivityAt = rt.CreatedAt,
            ExpiresAt = rt.ExpiresAt
        }).ToList();
    }

    public async Task<bool> RevokeSessionAsync(Guid sessionId, Guid userId, string reason = "user_action", CancellationToken ct = default)
    {
        var refreshToken = await _context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.Id == sessionId && rt.UserId == userId && rt.RevokedAt == null, ct);

        if (refreshToken == null)
        {
            return false;
        }

        refreshToken.RevokedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Session {SessionId} revoked for user {UserId}, reason: {Reason}",
            sessionId, userId, reason);

        return true;
    }

    public async Task<int> RevokeAllOtherSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct = default)
    {
        var count = await _context.RevokeRefreshTokensAsync(userId, currentSessionId, ct);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Revoked {Count} other sessions for user {UserId} (kept {CurrentSessionId})",
            count, userId, currentSessionId);

        return count;
    }

    public async Task<int> RevokeAllUserSessionsAsync(Guid userId, string reason = "admin_action", CancellationToken ct = default)
    {
        var count = await _context.RevokeRefreshTokensAsync(userId, ct);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Revoked all {Count} sessions for user {UserId}, reason: {Reason}",
            count, userId, reason);

        return count;
    }

    public async Task<int> CleanupExpiredSessionsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow;

        // Legacy: UserSessions is unused (no sign-in writes it); this only clears rows left over from before task 5568.
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

        IQueryable<RefreshToken> all = _context.RefreshTokens.AsNoTracking();

        if (tenantId.HasValue)
        {
            // Filter to users who have a role in the given tenant
            var tenantUserIds = _context.UserRoles
                .Where(ur => ur.TenantId == tenantId.Value)
                .Select(ur => ur.UserId)
                .Distinct();

            all = all.Where(rt => tenantUserIds.Contains(rt.UserId));
        }

        var active = await all.Where(rt => rt.RevokedAt == null && rt.ExpiresAt > now).ToListAsync(ct);

        return new SessionStatistics
        {
            TotalActiveSessions = active.Count,
            UniqueUsers = active.Select(rt => rt.UserId).Distinct().Count(),
            SessionsByDevice = active
                .GroupBy(rt => ParseDeviceInfo(rt.UserAgent) ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            SessionsCreatedToday = await all.CountAsync(rt => rt.CreatedAt >= todayStart, ct),
            SessionsRevokedToday = await all.CountAsync(rt => rt.RevokedAt >= todayStart, ct)
        };
    }

    // --- Private helpers ---

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
