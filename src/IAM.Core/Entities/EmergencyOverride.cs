namespace IAM.Core.Entities;

/// <summary>
/// Emergency override (break-glass) access for critical situations
/// Bypasses normal policy evaluation with full audit trail
/// </summary>
public class EmergencyOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// User who activated the emergency override
    /// </summary>
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Tenant context for override
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Override type
    /// </summary>
    public EmergencyOverrideType OverrideType { get; set; }

    /// <summary>
    /// Justification for emergency access (required)
    /// </summary>
    public string Justification { get; set; } = string.Empty;

    /// <summary>
    /// Resources being accessed (comma-separated)
    /// </summary>
    public string? AccessedResources { get; set; }

    /// <summary>
    /// Actions performed (comma-separated)
    /// </summary>
    public string? ActionsPerformed { get; set; }

    /// <summary>
    /// When override was activated
    /// </summary>
    public DateTime ActivatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When override expires (auto-revoke)
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When override was deactivated
    /// </summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>
    /// User who deactivated the override (if manually deactivated)
    /// </summary>
    public Guid? DeactivatedByUserId { get; set; }

    /// <summary>
    /// Current status
    /// </summary>
    public EmergencyOverrideStatus Status { get; set; } = EmergencyOverrideStatus.Active;

    /// <summary>
    /// Approval status (can require post-incident review)
    /// </summary>
    public EmergencyApprovalStatus ApprovalStatus { get; set; } = EmergencyApprovalStatus.PendingReview;

    /// <summary>
    /// User who approved/rejected the override use
    /// </summary>
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>
    /// Review comments
    /// </summary>
    public string? ReviewComments { get; set; }

    /// <summary>
    /// When review was completed
    /// </summary>
    public DateTime? ReviewedAt { get; set; }

    /// <summary>
    /// IP address where override was activated
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// Device ID where override was activated
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Incident ticket ID (if linked to incident management system)
    /// </summary>
    public string? IncidentTicketId { get; set; }

    /// <summary>
    /// Severity level of the emergency
    /// </summary>
    public EmergencySeverity Severity { get; set; } = EmergencySeverity.Medium;

    /// <summary>
    /// Whether notification was sent to security team
    /// </summary>
    public bool SecurityNotified { get; set; } = false;

    /// <summary>
    /// When security notification was sent
    /// </summary>
    public DateTime? SecurityNotifiedAt { get; set; }

    /// <summary>
    /// Check if override is currently active
    /// </summary>
    public bool IsActive()
    {
        return Status == EmergencyOverrideStatus.Active &&
               DateTime.UtcNow < ExpiresAt;
    }

    /// <summary>
    /// Deactivate override
    /// </summary>
    public void Deactivate(Guid? deactivatedBy = null)
    {
        Status = EmergencyOverrideStatus.Deactivated;
        DeactivatedAt = DateTime.UtcNow;
        if (deactivatedBy.HasValue)
        {
            DeactivatedByUserId = deactivatedBy.Value;
        }
    }
}

/// <summary>
/// Type of emergency override
/// </summary>
public enum EmergencyOverrideType
{
    /// <summary>
    /// Full system access (most privileged)
    /// </summary>
    FullAccess = 0,

    /// <summary>
    /// Elevated access within specific tenant
    /// </summary>
    ElevatedTenantAccess = 1,

    /// <summary>
    /// Specific resource access
    /// </summary>
    ResourceSpecific = 2,

    /// <summary>
    /// Bypass maintenance window
    /// </summary>
    MaintenanceBypass = 3,

    /// <summary>
    /// Bypass time restrictions
    /// </summary>
    TimeRestrictionBypass = 4
}

/// <summary>
/// Emergency override status
/// </summary>
public enum EmergencyOverrideStatus
{
    Active = 0,
    Deactivated = 1,
    Expired = 2,
    Revoked = 3
}

/// <summary>
/// Approval status for post-incident review
/// </summary>
public enum EmergencyApprovalStatus
{
    PendingReview = 0,
    Approved = 1,
    Rejected = 2,
    UnderInvestigation = 3
}

/// <summary>
/// Emergency severity level
/// </summary>
public enum EmergencySeverity
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}
