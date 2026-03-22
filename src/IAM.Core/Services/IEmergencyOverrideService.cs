using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for emergency override (break-glass) access management
/// </summary>
public interface IEmergencyOverrideService
{
    /// <summary>
    /// Activate emergency override
    /// </summary>
    Task<EmergencyOverride> ActivateOverrideAsync(
        Guid userId,
        Guid tenantId,
        EmergencyOverrideType overrideType,
        string justification,
        EmergencySeverity severity,
        int durationMinutes,
        string? incidentTicketId = null,
        string? ipAddress = null,
        string? deviceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivate emergency override
    /// </summary>
    Task<EmergencyOverride> DeactivateOverrideAsync(
        Guid overrideId,
        Guid deactivatedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if user has active emergency override
    /// </summary>
    Task<bool> HasActiveOverrideAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get active emergency overrides
    /// </summary>
    Task<List<EmergencyOverride>> GetActiveOverridesAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get emergency override history
    /// </summary>
    Task<List<EmergencyOverride>> GetOverrideHistoryAsync(
        Guid? userId = null,
        Guid? tenantId = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Review emergency override (post-incident)
    /// </summary>
    Task<EmergencyOverride> ReviewOverrideAsync(
        Guid overrideId,
        Guid reviewedByUserId,
        EmergencyApprovalStatus approvalStatus,
        string? reviewComments = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Process expired overrides (background job)
    /// </summary>
    Task<int> ProcessExpiredOverridesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send security notification for override activation
    /// </summary>
    Task SendSecurityNotificationAsync(
        EmergencyOverride emergencyOverride,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get overrides pending review
    /// </summary>
    Task<List<EmergencyOverride>> GetPendingReviewsAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);
}
