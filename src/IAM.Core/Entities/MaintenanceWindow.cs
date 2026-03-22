namespace IAM.Core.Entities;

/// <summary>
/// Scheduled maintenance window that affects policy evaluation
/// During maintenance, policies can be automatically suspended or modified
/// </summary>
public class MaintenanceWindow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Maintenance window name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the maintenance
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Tenant affected by this maintenance (null = system-wide)
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Maintenance window start time (UTC)
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Maintenance window end time (UTC)
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Timezone for display purposes (IANA format)
    /// </summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>
    /// Policy behavior during maintenance: Suspend, Allow, Deny, Unchanged
    /// </summary>
    public MaintenancePolicyBehavior PolicyBehavior { get; set; } = MaintenancePolicyBehavior.Unchanged;

    /// <summary>
    /// Whether to send notifications before maintenance
    /// </summary>
    public bool SendNotifications { get; set; } = true;

    /// <summary>
    /// Notification lead time in minutes
    /// </summary>
    public int NotificationLeadTimeMinutes { get; set; } = 60;

    /// <summary>
    /// Whether this is a recurring maintenance window
    /// </summary>
    public bool IsRecurring { get; set; } = false;

    /// <summary>
    /// Recurrence pattern (daily, weekly, monthly)
    /// </summary>
    public string? RecurrencePattern { get; set; }

    /// <summary>
    /// Current status of maintenance window
    /// </summary>
    public MaintenanceStatus Status { get; set; } = MaintenanceStatus.Scheduled;

    /// <summary>
    /// When maintenance was actually started (may differ from scheduled)
    /// </summary>
    public DateTime? ActualStartTime { get; set; }

    /// <summary>
    /// When maintenance was actually completed
    /// </summary>
    public DateTime? ActualEndTime { get; set; }

    /// <summary>
    /// Whether the maintenance window is currently active
    /// </summary>
    public bool IsActive()
    {
        var now = DateTime.UtcNow;
        return Status == MaintenanceStatus.InProgress &&
               now >= StartTime &&
               now <= EndTime;
    }

    /// <summary>
    /// Check if maintenance affects a specific tenant (considering hierarchy)
    /// </summary>
    public bool AffectsTenant(Guid tenantId)
    {
        // System-wide maintenance affects all tenants
        if (!TenantId.HasValue)
            return true;

        // Exact match
        if (TenantId.Value == tenantId)
            return true;

        // TODO: Check if tenantId is descendant of TenantId (requires hierarchy lookup)
        return false;
    }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// Policy behavior during maintenance window
/// </summary>
public enum MaintenancePolicyBehavior
{
    /// <summary>
    /// Policies continue to work normally
    /// </summary>
    Unchanged = 0,

    /// <summary>
    /// All policies are temporarily suspended (default deny unless emergency override)
    /// </summary>
    Suspend = 1,

    /// <summary>
    /// All access is allowed (bypass policies)
    /// </summary>
    Allow = 2,

    /// <summary>
    /// All access is denied (lockdown)
    /// </summary>
    Deny = 3
}

/// <summary>
/// Maintenance window status
/// </summary>
public enum MaintenanceStatus
{
    /// <summary>
    /// Maintenance is scheduled but not started
    /// </summary>
    Scheduled = 0,

    /// <summary>
    /// Maintenance is currently in progress
    /// </summary>
    InProgress = 1,

    /// <summary>
    /// Maintenance completed successfully
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Maintenance was cancelled
    /// </summary>
    Cancelled = 3,

    /// <summary>
    /// Maintenance was extended beyond scheduled time
    /// </summary>
    Extended = 4
}
