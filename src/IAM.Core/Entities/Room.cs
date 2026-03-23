namespace IAM.Core.Entities;

/// <summary>
/// Room within a floor
/// </summary>
public class Room
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public string? RoomNumber { get; set; } // e.g., "2.14", "A-205"

    /// <summary>
    /// Room type (office, meeting room, hallway, technical room, etc.)
    /// </summary>
    public RoomType Type { get; set; } = RoomType.Office;

    /// <summary>
    /// Parent floor
    /// </summary>
    public Guid FloorId { get; set; }
    public Floor Floor { get; set; } = null!;

    /// <summary>
    /// Tenant that owns this room
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// IoT devices in this room
    /// </summary>
    public ICollection<IoTDevice> Devices { get; set; } = new List<IoTDevice>();

    /// <summary>
    /// Room groups this room belongs to
    /// </summary>
    public ICollection<RoomGroupMembership> GroupMemberships { get; set; } = new List<RoomGroupMembership>();

    /// <summary>
    /// Permissions granted on this room
    /// </summary>
    public ICollection<ResourcePermission> Permissions { get; set; } = new List<ResourcePermission>();

    /// <summary>
    /// Area in square meters
    /// </summary>
    public double? AreaSquareMeters { get; set; }

    /// <summary>
    /// Capacity (number of people)
    /// </summary>
    public int? Capacity { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum RoomType
{
    Office,
    MeetingRoom,
    ConferenceRoom,
    Hallway,
    Lobby,
    TechnicalRoom,
    ServerRoom,
    Storage,
    Bathroom,
    Kitchen,
    Other
}
