namespace IAM.Core.Entities;

/// <summary>
/// Represents a geographic fence (circular region) for a tenant.
/// Access is only allowed from within the specified radius of the center point.
/// Uses Haversine formula for distance calculation.
/// </summary>
public class GeoFence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// Human-readable name (e.g., "Amsterdam Office", "Rotterdam Warehouse")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Center point latitude in decimal degrees
    /// </summary>
    public double Latitude { get; set; }

    /// <summary>
    /// Center point longitude in decimal degrees
    /// </summary>
    public double Longitude { get; set; }

    /// <summary>
    /// Radius in meters from the center point
    /// </summary>
    public double RadiusMeters { get; set; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Tenant Tenant { get; set; } = null!;
}
