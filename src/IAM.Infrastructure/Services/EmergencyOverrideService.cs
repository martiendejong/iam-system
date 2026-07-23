using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Implementation of emergency override (break-glass) service
/// </summary>
public class EmergencyOverrideService : IEmergencyOverrideService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<EmergencyOverrideService> _logger;
    private readonly IEventBus _eventBus;

    public EmergencyOverrideService(
        IAMDbContext context,
        ILogger<EmergencyOverrideService> logger,
        IEventBus eventBus)
    {
        _context = context;
        _logger = logger;
        _eventBus = eventBus;
    }

    private async Task PublishEventAsync(string eventType, object payload, Guid? tenantId)
    {
        try
        {
            await _eventBus.PublishAsync(eventType, payload, tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish {EventType} event", eventType);
        }
    }

    public async Task<EmergencyOverride> ActivateOverrideAsync(
        Guid userId,
        Guid tenantId,
        EmergencyOverrideType overrideType,
        string justification,
        EmergencySeverity severity,
        int durationMinutes,
        string? incidentTicketId = null,
        string? ipAddress = null,
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(justification))
            throw new ArgumentException("Justification is required for emergency override");

        if (durationMinutes <= 0 || durationMinutes > 1440) // Max 24 hours
            throw new ArgumentException("Duration must be between 1 and 1440 minutes");

        var emergencyOverride = new EmergencyOverride
        {
            UserId = userId,
            TenantId = tenantId,
            OverrideType = overrideType,
            Justification = justification,
            Severity = severity,
            ExpiresAt = DateTime.UtcNow.AddMinutes(durationMinutes),
            IncidentTicketId = incidentTicketId,
            IpAddress = ipAddress,
            DeviceId = deviceId,
            Status = EmergencyOverrideStatus.Active,
            ApprovalStatus = EmergencyApprovalStatus.PendingReview
        };

        _context.EmergencyOverrides.Add(emergencyOverride);
        await _context.SaveChangesAsync(cancellationToken);

        // Send security notification (async fire-and-forget in production)
        await SendSecurityNotificationAsync(emergencyOverride, cancellationToken);

        await PublishEventAsync(IamEventTypes.EmergencyOverrideActivated, new
        {
            overrideId = emergencyOverride.Id,
            userId,
            tenantId,
            overrideType = overrideType.ToString(),
            severity = severity.ToString(),
            justification,
            expiresAt = emergencyOverride.ExpiresAt
        }, tenantId);

        _logger.LogWarning(
            "EMERGENCY OVERRIDE ACTIVATED: User:{UserId} Type:{Type} Severity:{Severity} Duration:{Duration}min Justification:{Justification}",
            userId, overrideType, severity, durationMinutes, justification);

        return emergencyOverride;
    }

    public async Task<EmergencyOverride> DeactivateOverrideAsync(
        Guid overrideId,
        Guid deactivatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var emergencyOverride = await _context.EmergencyOverrides
            .FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken);

        if (emergencyOverride == null)
            throw new ArgumentException($"Emergency override {overrideId} not found");

        if (emergencyOverride.Status != EmergencyOverrideStatus.Active)
            throw new InvalidOperationException($"Override is not active (current status: {emergencyOverride.Status})");

        emergencyOverride.Deactivate(deactivatedByUserId);
        await _context.SaveChangesAsync(cancellationToken);

        await PublishEventAsync(IamEventTypes.EmergencyOverrideDeactivated, new
        {
            overrideId = emergencyOverride.Id,
            tenantId = emergencyOverride.TenantId,
            deactivatedByUserId
        }, emergencyOverride.TenantId);

        _logger.LogWarning(
            "EMERGENCY OVERRIDE DEACTIVATED: Override:{OverrideId} DeactivatedBy:{UserId}",
            overrideId, deactivatedByUserId);

        return emergencyOverride;
    }

    public async Task<bool> HasActiveOverrideAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        return await _context.EmergencyOverrides
            .AnyAsync(o =>
                o.UserId == userId &&
                o.TenantId == tenantId &&
                o.Status == EmergencyOverrideStatus.Active &&
                o.ExpiresAt > DateTime.UtcNow,
                cancellationToken);
    }

    public async Task<List<EmergencyOverride>> GetActiveOverridesAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.EmergencyOverrides
            .Where(o => o.Status == EmergencyOverrideStatus.Active && o.ExpiresAt > DateTime.UtcNow);

        if (tenantId.HasValue)
            query = query.Where(o => o.TenantId == tenantId.Value);

        return await query
            .OrderByDescending(o => o.Severity)
            .ThenBy(o => o.ExpiresAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<EmergencyOverride>> GetOverrideHistoryAsync(
        Guid? userId = null,
        Guid? tenantId = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var query = _context.EmergencyOverrides.AsQueryable();

        if (userId.HasValue)
            query = query.Where(o => o.UserId == userId.Value);

        if (tenantId.HasValue)
            query = query.Where(o => o.TenantId == tenantId.Value);

        if (startDate.HasValue)
            query = query.Where(o => o.ActivatedAt >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(o => o.ActivatedAt <= endDate.Value);

        return await query
            .OrderByDescending(o => o.ActivatedAt)
            .Skip(skip)
            .Take(Math.Min(take, 1000))
            .ToListAsync(cancellationToken);
    }

    public async Task<EmergencyOverride> ReviewOverrideAsync(
        Guid overrideId,
        Guid reviewedByUserId,
        EmergencyApprovalStatus approvalStatus,
        string? reviewComments = null,
        CancellationToken cancellationToken = default)
    {
        var emergencyOverride = await _context.EmergencyOverrides
            .FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken);

        if (emergencyOverride == null)
            throw new ArgumentException($"Emergency override {overrideId} not found");

        emergencyOverride.ApprovalStatus = approvalStatus;
        emergencyOverride.ReviewedByUserId = reviewedByUserId;
        emergencyOverride.ReviewComments = reviewComments;
        emergencyOverride.ReviewedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Emergency override reviewed: Override:{OverrideId} Status:{Status} ReviewedBy:{UserId}",
            overrideId, approvalStatus, reviewedByUserId);

        return emergencyOverride;
    }

    public async Task<int> ProcessExpiredOverridesAsync(CancellationToken cancellationToken = default)
    {
        var expiredOverrides = await _context.EmergencyOverrides
            .Where(o =>
                o.Status == EmergencyOverrideStatus.Active &&
                o.ExpiresAt <= DateTime.UtcNow)
            .ToListAsync(cancellationToken);

        foreach (var emergencyOverride in expiredOverrides)
        {
            emergencyOverride.Status = EmergencyOverrideStatus.Expired;
            emergencyOverride.DeactivatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (expiredOverrides.Count > 0)
        {
            _logger.LogInformation(
                "Processed {Count} expired emergency overrides",
                expiredOverrides.Count);
        }

        return expiredOverrides.Count;
    }

    public async Task SendSecurityNotificationAsync(
        EmergencyOverride emergencyOverride,
        CancellationToken cancellationToken = default)
    {
        // In production, this would integrate with notification system (email, Slack, etc.)
        // For now, just log and mark as notified

        emergencyOverride.SecurityNotified = true;
        emergencyOverride.SecurityNotifiedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogCritical(
            "SECURITY ALERT: Emergency Override Activated - User:{UserId} Type:{Type} Severity:{Severity} Justification:{Justification}",
            emergencyOverride.UserId,
            emergencyOverride.OverrideType,
            emergencyOverride.Severity,
            emergencyOverride.Justification);
    }

    public async Task<List<EmergencyOverride>> GetPendingReviewsAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.EmergencyOverrides
            .Where(o => o.ApprovalStatus == EmergencyApprovalStatus.PendingReview);

        if (tenantId.HasValue)
            query = query.Where(o => o.TenantId == tenantId.Value);

        return await query
            .OrderByDescending(o => o.Severity)
            .ThenBy(o => o.ActivatedAt)
            .ToListAsync(cancellationToken);
    }
}
