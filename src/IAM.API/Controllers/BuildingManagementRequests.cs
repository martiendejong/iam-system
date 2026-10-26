using System.ComponentModel.DataAnnotations;
using IAM.Core.Entities;

namespace IAM.API.Controllers;

// Input models of the building-management API (task 5164). The controllers used to bind the database entities
// straight from the body, so a caller could set Id, TenantId, timestamps, IsActive, status fields and linked
// collections. These models carry only what a caller may choose; the tenant always comes from the caller's
// authorization and the rest is set by the server.

public class LocationRequest
{
    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [StringLength(500)]
    public string? Address { get; init; }

    [StringLength(200)]
    public string? City { get; init; }

    [StringLength(200)]
    public string? Country { get; init; }

    [Range(-90, 90)]
    public double? Latitude { get; init; }

    [Range(-180, 180)]
    public double? Longitude { get; init; }
}

public class BuildingRequest
{
    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [StringLength(50)]
    public string? Code { get; init; }

    [Required]
    public Guid LocationId { get; init; }

    [Range(0, 1000)]
    public int? TotalFloors { get; init; }
}

public class FloorRequest
{
    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [Range(-100, 1000)]
    public int FloorNumber { get; init; }

    [Required]
    public Guid BuildingId { get; init; }

    [Range(0, 10_000_000)]
    public double? AreaSquareMeters { get; init; }
}

public class RoomRequest
{
    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [StringLength(50)]
    public string? RoomNumber { get; init; }

    public RoomType Type { get; init; } = RoomType.Office;

    [Required]
    public Guid FloorId { get; init; }

    [Range(0, 10_000_000)]
    public double? AreaSquareMeters { get; init; }

    [Range(0, 100_000)]
    public int? Capacity { get; init; }
}

public class RoomGroupRequest
{
    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; init; }

    public Guid? FloorId { get; init; }

    public Guid? BuildingId { get; init; }
}

public class IoTDeviceRequest
{
    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Unique device identifier (MAC address, serial number, ...).</summary>
    [Required, StringLength(200)]
    public string DeviceId { get; init; } = string.Empty;

    public DeviceType Type { get; init; } = DeviceType.Sensor;

    [StringLength(200)]
    public string? Manufacturer { get; init; }

    [StringLength(200)]
    public string? Model { get; init; }

    [Required]
    public Guid RoomId { get; init; }

    public bool SupportsStreaming { get; init; }

    [StringLength(2000)]
    public string? StreamUrl { get; init; }

    [StringLength(50)]
    public string? StreamProtocol { get; init; }

    [StringLength(100)]
    public string? IpAddress { get; init; }

    [Range(0, 65535)]
    public int? Port { get; init; }

    [StringLength(10_000)]
    public string? Capabilities { get; init; }

    [StringLength(10_000)]
    public string? Metadata { get; init; }
}
