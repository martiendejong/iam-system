namespace IAM.Core.Entities;

/// <summary>
/// IoT device (sensor, camera, controller, etc.) within a room
/// </summary>
public class IoTDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Unique device identifier (MAC address, serial number, etc.)
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Device type (camera, temperature sensor, door lock, etc.)
    /// </summary>
    public DeviceType Type { get; set; } = DeviceType.Sensor;

    /// <summary>
    /// Manufacturer and model information
    /// </summary>
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }

    /// <summary>
    /// Parent room
    /// </summary>
    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    /// <summary>
    /// Tenant that owns this device
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Streaming configuration
    /// </summary>
    public bool SupportsStreaming { get; set; } = false;
    public string? StreamUrl { get; set; }
    public string? StreamProtocol { get; set; } // RTSP, WebRTC, HLS, etc.

    /// <summary>
    /// Connection status
    /// </summary>
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastDataAt { get; set; }

    /// <summary>
    /// IP address and network information
    /// </summary>
    public string? IpAddress { get; set; }
    public int? Port { get; set; }

    /// <summary>
    /// Device capabilities and metadata
    /// </summary>
    public string? Capabilities { get; set; } // JSON string with device-specific capabilities
    public string? Metadata { get; set; } // JSON string with custom metadata

    /// <summary>
    /// Permissions granted on this device
    /// </summary>
    public ICollection<ResourcePermission> Permissions { get; set; } = new List<ResourcePermission>();

    /// <summary>
    /// Audit trail for device access
    /// </summary>
    public ICollection<DeviceAccessLog> AccessLogs { get; set; } = new List<DeviceAccessLog>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum DeviceType
{
    Sensor,              // Generic sensor
    TemperatureSensor,
    HumiditySensor,
    MotionSensor,
    DoorSensor,
    WindowSensor,
    SmokeSensor,
    CO2Sensor,
    Camera,              // Security camera
    IPCamera,
    PTZCamera,
    DoorLock,
    AccessControl,
    Thermostat,
    LightController,
    BlindsController,
    HVAC,
    Actuator,
    Gateway,
    Other
}

public enum DeviceStatus
{
    Online,
    Offline,
    Maintenance,
    Error,
    Unknown
}
