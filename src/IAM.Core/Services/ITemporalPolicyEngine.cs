using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing temporal aspects of policies
/// Integrates with IPolicyInheritanceEngine for time-based access control
/// </summary>
public interface ITemporalPolicyEngine
{
    /// <summary>
    /// Check if a policy is active at a given time considering schedules and maintenance windows
    /// </summary>
    Task<bool> IsPolicyActiveAtAsync(
        Guid policyId,
        DateTime dateTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get active maintenance windows affecting a tenant
    /// </summary>
    Task<List<MaintenanceWindow>> GetActiveMaintenanceWindowsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get active temporary access grants for a user
    /// </summary>
    Task<List<TemporaryAccessGrant>> GetActiveTemporaryAccessAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grant temporary access to a user
    /// </summary>
    Task<TemporaryAccessGrant> GrantTemporaryAccessAsync(
        Guid userId,
        Guid roleId,
        Guid tenantId,
        DateTime startTime,
        DateTime endTime,
        string justification,
        Guid grantedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revoke temporary access grant
    /// </summary>
    Task<bool> RevokeTemporaryAccessAsync(
        Guid grantId,
        Guid revokedByUserId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Process expired temporary access grants (cleanup job)
    /// </summary>
    Task<int> ProcessExpiredGrantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Update maintenance window status based on current time (background job)
    /// </summary>
    Task<int> UpdateMaintenanceWindowStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if tenant is under maintenance and get policy behavior
    /// </summary>
    Task<(bool IsUnderMaintenance, MaintenancePolicyBehavior? Behavior)> CheckMaintenanceStatusAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
