namespace IAM.Core.Entities;

/// <summary>
/// Configurable rule that defines conditions for triggering security alerts.
/// Supports built-in rules (brute force, impossible travel, etc.) and custom rules.
/// </summary>
public class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Human-readable name (e.g., "Brute Force Detection", "Impossible Travel")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// JSON condition defining when this rule triggers.
    /// Example: { "type": "brute_force", "threshold": 5, "windowMinutes": 5 }
    /// </summary>
    public string Condition { get; set; } = "{}";

    /// <summary>
    /// Alert severity level
    /// </summary>
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;

    /// <summary>
    /// JSON array of notification channels.
    /// Example: [{ "type": "email", "target": "admin@example.com" }, { "type": "webhook", "target": "https://..." }]
    /// </summary>
    public string Channels { get; set; } = "[]";

    /// <summary>
    /// Minimum minutes between repeated alerts for the same condition to prevent alert fatigue
    /// </summary>
    public int CooldownMinutes { get; set; } = 15;

    /// <summary>
    /// Optional auto-response action when alert triggers.
    /// Values: null, "lock_account", "force_mfa", "kill_sessions"
    /// </summary>
    public string? AutoResponseAction { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SecurityAlert> Alerts { get; set; } = new List<SecurityAlert>();
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}
