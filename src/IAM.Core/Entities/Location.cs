namespace IAM.Core.Entities;

/// <summary>
/// Top-level location (campus, site, multi-building complex)
/// </summary>
public class Location
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Location name (e.g., "Hoofdkantoor Amsterdam", "Campus Utrecht")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>
    /// Tenant that owns this location
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Buildings at this location
    /// </summary>
    public ICollection<Building> Buildings { get; set; } = new List<Building>();

    /// <summary>
    /// Permissions granted on this location
    /// </summary>
    public ICollection<ResourcePermission> Permissions { get; set; } = new List<ResourcePermission>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
