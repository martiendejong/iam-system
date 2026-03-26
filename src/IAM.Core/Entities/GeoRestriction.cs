namespace IAM.Core.Entities;

/// <summary>
/// Represents country-level geographic access restrictions for a tenant.
/// Uses ISO 3166-1 alpha-2 country codes stored as JSON arrays.
/// Either AllowedCountries or BlockedCountries should be populated, not both.
/// </summary>
public class GeoRestriction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// JSON array of allowed ISO 3166-1 alpha-2 country codes (e.g., ["NL","DE","US"]).
    /// If set, only these countries are allowed.
    /// </summary>
    public string? AllowedCountries { get; set; }

    /// <summary>
    /// JSON array of blocked ISO 3166-1 alpha-2 country codes (e.g., ["CN","RU"]).
    /// If set, these countries are blocked and all others allowed.
    /// </summary>
    public string? BlockedCountries { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Tenant Tenant { get; set; } = null!;
}
