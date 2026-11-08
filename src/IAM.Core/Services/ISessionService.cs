using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Task 5568: a session is a real sign-in, i.e. an active refresh token; its id is the refresh token id.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Get all active (non-revoked, non-expired) sessions for a user.
    /// </summary>
    Task<List<UserSession>> GetActiveSessionsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Revoke a specific session. Returns false if session not found or not owned by the user.
    /// </summary>
    Task<bool> RevokeSessionAsync(Guid sessionId, Guid userId, string reason = "user_action", CancellationToken ct = default);

    /// <summary>
    /// Revoke all sessions for a user except the specified current session. Returns count revoked.
    /// </summary>
    Task<int> RevokeAllOtherSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct = default);

    /// <summary>
    /// Revoke all sessions for a user (admin action or password reset). Returns count revoked.
    /// </summary>
    Task<int> RevokeAllUserSessionsAsync(Guid userId, string reason = "admin_action", CancellationToken ct = default);

    /// <summary>
    /// Remove leftover rows of the unused UserSessions table. Returns count cleaned up.
    /// </summary>
    Task<int> CleanupExpiredSessionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get aggregate session statistics, optionally scoped to a tenant.
    /// </summary>
    Task<SessionStatistics> GetStatisticsAsync(Guid? tenantId = null, CancellationToken ct = default);
}

public class SessionStatistics
{
    public int TotalActiveSessions { get; set; }
    public int UniqueUsers { get; set; }
    public Dictionary<string, int> SessionsByDevice { get; set; } = new();
    public int SessionsCreatedToday { get; set; }
    public int SessionsRevokedToday { get; set; }
}
