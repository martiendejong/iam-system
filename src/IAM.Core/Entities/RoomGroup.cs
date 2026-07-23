namespace IAM.Core.Entities;

/// <summary>
/// Logical grouping of rooms for collective permission management
/// Examples: "East Wing Offices", "Server Rooms", "Executive Suites"
/// </summary>
public class RoomGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Parent floor (optional - can span multiple floors)
    /// </summary>
    public Guid? FloorId { get; set; }
    public Floor? Floor { get; set; }

    /// <summary>
    /// Parent building (optional - can span multiple buildings)
    /// </summary>
    public Guid? BuildingId { get; set; }
    public Building? Building { get; set; }

    /// <summary>
    /// Tenant that owns this room group
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Rooms that belong to this group
    /// </summary>
    public ICollection<RoomGroupMembership> RoomMemberships { get; set; } = new List<RoomGroupMembership>();

    /// <summary>
    /// Permissions granted on this room group
    /// </summary>
    public ICollection<ResourcePermission> Permissions { get; set; } = new List<ResourcePermission>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
