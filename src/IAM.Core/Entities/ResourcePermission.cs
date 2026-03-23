namespace IAM.Core.Entities;

/// <summary>
/// Permission granted to a user/role on a specific resource in the hierarchy
/// Supports: Location, Building, Floor, Room, RoomGroup, IoTDevice
/// </summary>
public class ResourcePermission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Who has the permission (user or role)
    /// </summary>
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public Guid? RoleId { get; set; }
    public Role? Role { get; set; }

    /// <summary>
    /// What resource type is being granted permission on
    /// </summary>
    public ResourceType ResourceType { get; set; }

    /// <summary>
    /// Resource ID (Location/Building/Floor/Room/RoomGroup/IoTDevice)
    /// </summary>
    public Guid ResourceId { get; set; }

    /// <summary>
    /// Navigation properties for each resource type
    /// Only one will be set based on ResourceType
    /// </summary>
    public Guid? LocationId { get; set; }
    public Location? Location { get; set; }

    public Guid? BuildingId { get; set; }
    public Building? Building { get; set; }

    public Guid? FloorId { get; set; }
    public Floor? Floor { get; set; }

    public Guid? RoomId { get; set; }
    public Room? Room { get; set; }

    public Guid? RoomGroupId { get; set; }
    public RoomGroup? RoomGroup { get; set; }

    public Guid? IoTDeviceId { get; set; }
    public IoTDevice? IoTDevice { get; set; }

    /// <summary>
    /// What actions are allowed
    /// </summary>
    public PermissionAction Actions { get; set; }

    /// <summary>
    /// Whether permissions inherit to child resources
    /// Example: Permission on Building inherits to all Floors/Rooms/Devices in that building
    /// </summary>
    public bool InheritToChildren { get; set; } = true;

    /// <summary>
    /// Tenant that owns this permission
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Time restrictions (optional)
    /// </summary>
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }

    /// <summary>
    /// Who granted this permission
    /// </summary>
    public Guid? GrantedByUserId { get; set; }
    public User? GrantedByUser { get; set; }

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional reason for granting this permission
    /// </summary>
    public string? Reason { get; set; }
}

public enum ResourceType
{
    Location,
    Building,
    Floor,
    Room,
    RoomGroup,
    IoTDevice
}

/// <summary>
/// Bitwise flags for permission actions
/// </summary>
[Flags]
public enum PermissionAction
{
    None = 0,

    // Basic actions
    View = 1 << 0,           // 1 - View resource information
    Edit = 1 << 1,           // 2 - Edit resource configuration
    Delete = 1 << 2,         // 4 - Delete resource
    Create = 1 << 3,         // 8 - Create child resources

    // Device-specific actions
    Stream = 1 << 4,         // 16 - Stream from device (video/audio/data)
    Control = 1 << 5,        // 32 - Send control commands to device
    Configure = 1 << 6,      // 64 - Configure device settings

    // Management actions
    ManageAccess = 1 << 7,   // 128 - Grant/revoke permissions to others
    ViewAudit = 1 << 8,      // 256 - View audit logs

    // Convenience combinations
    ReadOnly = View | ViewAudit,                                    // 257
    Operator = View | Stream | Control,                             // 49
    Manager = View | Edit | Create | ManageAccess | ViewAudit,     // 395
    FullAccess = View | Edit | Delete | Create | Stream | Control | Configure | ManageAccess | ViewAudit  // 511
}
