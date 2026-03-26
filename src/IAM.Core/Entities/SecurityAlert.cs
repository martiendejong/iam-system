namespace IAM.Core.Entities;

/// <summary>
/// A fired security alert instance triggered by an AlertRule.
/// Tracks acknowledgement and any auto-response actions taken.
/// </summary>
public class SecurityAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid RuleId { get; set; }
    public AlertRule? Rule { get; set; }

    public AlertSeverity Severity { get; set; }

    /// <summary>
    /// Short alert title (e.g., "Brute force attack detected for user@example.com")
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// JSON details about the alert (affected user, IPs, timestamps, etc.)
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// When the alert was acknowledged by an admin (null = unacknowledged)
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// User who acknowledged the alert
    /// </summary>
    public Guid? AcknowledgedByUserId { get; set; }
    public User? AcknowledgedByUser { get; set; }

    /// <summary>
    /// Auto-response action that was executed (e.g., "lock_account", "force_mfa", "kill_sessions")
    /// </summary>
    public string? AutoResponseAction { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsAcknowledged => AcknowledgedAt.HasValue;
}
