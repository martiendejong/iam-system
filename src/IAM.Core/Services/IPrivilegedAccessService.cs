using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for Privileged Access Management (PAM).
/// Manages time-boxed privilege elevation with check-out/check-in,
/// break-glass emergency access, and auto de-escalation.
/// </summary>
public interface IPrivilegedAccessService
{
    // ─── Session Management ──────────────────────────────────

    /// <summary>
    /// Request checkout of a privileged role (creates a pending or active session)
    /// </summary>
    Task<PrivilegedSession> CheckoutAsync(
        Guid userId,
        Guid roleId,
        Guid tenantId,
        string justification,
        int? durationMinutes = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check in (release) a privileged role
    /// </summary>
    Task<PrivilegedSession> CheckinAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approve a pending privileged session
    /// </summary>
    Task<PrivilegedSession> ApproveSessionAsync(
        Guid sessionId,
        Guid approverUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deny a pending privileged session
    /// </summary>
    Task<PrivilegedSession> DenySessionAsync(
        Guid sessionId,
        Guid approverUserId,
        CancellationToken cancellationToken = default);

    // ─── Break-Glass Emergency Access ────────────────────────

    /// <summary>
    /// Request break-glass emergency access (requires multiple approvers, 4-eyes principle)
    /// </summary>
    Task<PrivilegedSession> BreakGlassAsync(
        Guid userId,
        Guid roleId,
        Guid tenantId,
        string justification,
        List<Guid> approverUserIds,
        int? durationMinutes = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    // ─── Session Queries ─────────────────────────────────────

    /// <summary>
    /// Get active privileged sessions
    /// </summary>
    Task<List<PrivilegedSession>> GetActiveSessionsAsync(
        Guid? tenantId = null,
        Guid? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get session history with filtering
    /// </summary>
    Task<List<PrivilegedSession>> GetSessionHistoryAsync(
        Guid? userId = null,
        Guid? tenantId = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if user has an active privileged session for a specific role
    /// </summary>
    Task<bool> HasActiveSessionAsync(
        Guid userId,
        Guid roleId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    // ─── Auto De-escalation ──────────────────────────────────

    /// <summary>
    /// Process expired sessions (auto de-escalation background job)
    /// </summary>
    Task<int> ProcessExpiredSessionsAsync(CancellationToken cancellationToken = default);

    // ─── PAM Policy Management ───────────────────────────────

    /// <summary>
    /// Get all PAM policies for a tenant
    /// </summary>
    Task<List<PamPolicy>> GetPoliciesAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get PAM policy by ID
    /// </summary>
    Task<PamPolicy?> GetPolicyByIdAsync(
        Guid policyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get PAM policy for a specific role/tenant combination
    /// </summary>
    Task<PamPolicy?> GetPolicyForRoleAsync(
        Guid roleId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a PAM policy
    /// </summary>
    Task<PamPolicy> CreatePolicyAsync(
        PamPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update a PAM policy
    /// </summary>
    Task<PamPolicy> UpdatePolicyAsync(
        PamPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a PAM policy
    /// </summary>
    Task DeletePolicyAsync(
        Guid policyId,
        CancellationToken cancellationToken = default);
}
