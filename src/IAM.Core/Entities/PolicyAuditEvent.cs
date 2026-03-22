namespace IAM.Core.Entities;

/// <summary>
/// Detailed audit event for policy-related actions
/// Tracks all policy changes, evaluations, and access decisions
/// </summary>
public class PolicyAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Type of audit event
    /// </summary>
    public PolicyAuditEventType EventType { get; set; }

    /// <summary>
    /// Policy that triggered this event (if applicable)
    /// </summary>
    public Guid? PolicyId { get; set; }
    public Policy? Policy { get; set; }

    /// <summary>
    /// User involved in this event
    /// </summary>
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Tenant context for this event
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Resource being accessed (for evaluation events)
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>
    /// Action being performed (for evaluation events)
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// Result of policy evaluation (Allow/Deny)
    /// </summary>
    public bool? WasAllowed { get; set; }

    /// <summary>
    /// Reason for the decision
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// IP address of the requester
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// Device ID (if available)
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// User agent string
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Policy changes (before/after for modifications) in JSON format
    /// </summary>
    public string? Changes { get; set; }

    /// <summary>
    /// Additional metadata in JSON format
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>
    /// Policy evaluation duration in milliseconds
    /// </summary>
    public long? EvaluationTimeMs { get; set; }

    /// <summary>
    /// Number of policies evaluated (for evaluation events)
    /// </summary>
    public int? PoliciesEvaluatedCount { get; set; }

    /// <summary>
    /// Risk score (0-100) based on anomaly detection
    /// </summary>
    public int? RiskScore { get; set; }

    /// <summary>
    /// Whether this event triggered an alert
    /// </summary>
    public bool TriggeredAlert { get; set; } = false;

    /// <summary>
    /// Alert details (if TriggeredAlert is true)
    /// </summary>
    public string? AlertDetails { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Type of policy audit event
/// </summary>
public enum PolicyAuditEventType
{
    // Policy lifecycle events
    PolicyCreated = 0,
    PolicyModified = 1,
    PolicyDeleted = 2,
    PolicyActivated = 3,
    PolicyDeactivated = 4,
    PolicyExpired = 5,

    // Policy evaluation events
    AccessEvaluated = 10,
    AccessGranted = 11,
    AccessDenied = 12,

    // Administrative events
    PolicyInheritanceChanged = 20,
    PolicyPriorityChanged = 21,
    PolicyScheduleChanged = 22,

    // Temporary access events
    TemporaryAccessGranted = 30,
    TemporaryAccessRevoked = 31,
    TemporaryAccessExpired = 32,
    TemporaryAccessExtended = 33,

    // Maintenance events
    MaintenanceStarted = 40,
    MaintenanceCompleted = 41,
    MaintenanceCancelled = 42,

    // Compliance events
    ComplianceViolation = 50,
    UnusualAccessPattern = 51,
    FailedAccessAttempt = 52,
    MultipleFailedAttempts = 53,

    // System events
    PolicyCacheCleared = 60,
    PolicySimulated = 61,
    PolicyImpactAnalyzed = 62
}
