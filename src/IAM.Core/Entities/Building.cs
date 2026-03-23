namespace IAM.Core.Entities;

/// <summary>
/// Building within a location
/// </summary>
public class Building
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; } // Building code (e.g., "A", "B", "North Wing")

    /// <summary>
    /// Parent location
    /// </summary>
    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;

    /// <summary>
    /// Tenant that owns this building
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Floors in this building
    /// </summary>
    public ICollection<Floor> Floors { get; set; } = new List<Floor>();

    /// <summary>
    /// Permissions granted on this building
    /// </summary>
    public ICollection<ResourcePermission> Permissions { get; set; } = new List<ResourcePermission>();

    /// <summary>
    /// Total number of floors
    /// </summary>
    public int? TotalFloors { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
