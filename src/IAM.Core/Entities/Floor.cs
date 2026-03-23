namespace IAM.Core.Entities;

/// <summary>
/// Floor/level within a building
/// </summary>
public class Floor
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Floor number (0 = ground, -1 = basement, etc.)
    /// </summary>
    public int FloorNumber { get; set; }

    /// <summary>
    /// Parent building
    /// </summary>
    public Guid BuildingId { get; set; }
    public Building Building { get; set; } = null!;

    /// <summary>
    /// Tenant that owns this floor
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Rooms on this floor
    /// </summary>
    public ICollection<Room> Rooms { get; set; } = new List<Room>();

    /// <summary>
    /// Permissions granted on this floor
    /// </summary>
    public ICollection<ResourcePermission> Permissions { get; set; } = new List<ResourcePermission>();

    /// <summary>
    /// Total area in square meters
    /// </summary>
    public double? AreaSquareMeters { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
