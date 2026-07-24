namespace IAM.Core.Entities;

/// <summary>
/// Many-to-many relationship between Rooms and RoomGroups
/// </summary>
public class RoomGroupMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    public Guid RoomGroupId { get; set; }
    public RoomGroup? RoomGroup { get; set; }

    /// <summary>
    /// When this room was added to the group
    /// </summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional notes about why this room is in this group
    /// </summary>
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;
}
