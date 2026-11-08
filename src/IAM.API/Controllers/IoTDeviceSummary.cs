using IAM.Core.Entities;

namespace IAM.API.Controllers;

/// <summary>
/// Task 5571: what a plain tenant member gets back when reading IoT devices. It has no stream address, protocol,
/// IP address, port, capabilities, metadata or hardware identifier: camera stream addresses often embed a username
/// and password or a token, and the per-device Stream permission (and its access log) is only enforced by the
/// request-stream action. Managers (SuperAdmin, TenantAdmin, BuildingOwner, BuildingManager) still get the full
/// <see cref="IoTDevice"/>.
/// </summary>
public sealed class IoTDeviceSummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DeviceType Type { get; init; }
    public Guid RoomId { get; init; }
    public Guid TenantId { get; init; }
    public bool SupportsStreaming { get; init; }
    public DeviceStatus Status { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsActive { get; init; }

    public static IoTDeviceSummary From(IoTDevice d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Type = d.Type,
        RoomId = d.RoomId,
        TenantId = d.TenantId,
        SupportsStreaming = d.SupportsStreaming,
        Status = d.Status,
        LastSeenAt = d.LastSeenAt,
        CreatedAt = d.CreatedAt,
        IsActive = d.IsActive
    };

    /// <summary>The single mapping every device read goes through: full device for managers, summary otherwise.</summary>
    public static object ForReader(IoTDevice device, bool canManage) =>
        canManage ? device : From(device);

    public static object ForReader(IEnumerable<IoTDevice> devices, bool canManage) =>
        canManage ? devices : devices.Select(From).ToList();
}
