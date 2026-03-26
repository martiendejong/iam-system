namespace IAM.Core.Entities;

/// <summary>
/// Data Processing Agreement (DPA) document for a tenant.
/// Supports GDPR Article 28 requirements for processor agreements.
/// </summary>
public class DataProcessingAgreement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant this agreement belongs to
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Human-readable name of the agreement
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Agreement version (e.g., "1.0", "2.1")
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// When this agreement version becomes effective
    /// </summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Full content of the agreement in Markdown format
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Whether users must explicitly consent to this agreement
    /// </summary>
    public bool RequiresExplicitConsent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
