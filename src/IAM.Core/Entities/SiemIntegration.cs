namespace IAM.Core.Entities;

/// <summary>
/// Configuration for SIEM (Security Information and Event Management) integration.
/// Supports exporting security events to external SIEM platforms.
/// </summary>
public class SiemIntegration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Human-readable name (e.g., "Production Splunk", "Azure Sentinel")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Integration type
    /// </summary>
    public SiemType Type { get; set; }

    /// <summary>
    /// Endpoint URL for the SIEM platform
    /// </summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// JSON authentication configuration (API keys, tokens, certificates)
    /// Example: { "apiKey": "...", "index": "iam-events" }
    /// </summary>
    public string? AuthConfig { get; set; }

    /// <summary>
    /// Output format: CEF, JSON, Syslog, LEEF
    /// </summary>
    public string Format { get; set; } = "JSON";

    /// <summary>
    /// Which event types to forward. JSON array, or null for all events.
    /// Example: ["security.alert", "auth.login_failed", "user.locked"]
    /// </summary>
    public string? EventFilter { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum SiemType
{
    Syslog = 0,
    Webhook = 1,
    Splunk = 2,
    Elastic = 3,
    AzureSentinel = 4
}
