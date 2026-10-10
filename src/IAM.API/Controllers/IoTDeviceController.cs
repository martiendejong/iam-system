using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

// Task 5164: authorization and tenant resolution live in BuildingManagementControllerBase. A device token can no
// longer reach this controller at all (it used to be enough to create, change and delete every device of its
// tenant); a device that has to report in uses the device-auth endpoints.
[Route("api/[controller]")]
public class IoTDeviceController : BuildingManagementControllerBase
{
    private readonly IIoTDeviceService _deviceService;
    private readonly IRoomService _roomService;
    private readonly IResourcePermissionService _permissionService;

    public IoTDeviceController(
        IIoTDeviceService deviceService,
        IRoomService roomService,
        IResourcePermissionService permissionService,
        ITenantAccessResolver access) : base(access)
    {
        _deviceService = deviceService;
        _roomService = roomService;
        _permissionService = permissionService;
    }

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            throw new UnauthorizedAccessException("User ID not found in token");
        }
        return userId;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<IoTDevice>), 200)]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetAll([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var devices = await _deviceService.GetAllAsync(scope.TenantId, includeInactive);
        return Ok(devices);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(IoTDevice), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IoTDevice>> GetById(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var device = await _deviceService.GetByIdAsync(id, scope.TenantId);

        if (device == null)
            return NotFound();

        return Ok(device);
    }

    [HttpGet("device-id/{deviceId}")]
    [ProducesResponseType(typeof(IoTDevice), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IoTDevice>> GetByDeviceId(string deviceId, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var device = await _deviceService.GetByDeviceIdAsync(deviceId, scope.TenantId);

        if (device == null)
            return NotFound();

        return Ok(device);
    }

    [HttpGet("room/{roomId}")]
    [ProducesResponseType(typeof(IEnumerable<IoTDevice>), 200)]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetByRoom(Guid roomId, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var devices = await _deviceService.GetByRoomAsync(roomId, scope.TenantId);
        return Ok(devices);
    }

    [HttpGet("type/{type}")]
    [ProducesResponseType(typeof(IEnumerable<IoTDevice>), 200)]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetByType(DeviceType type, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var devices = await _deviceService.GetByTypeAsync(type, scope.TenantId);
        return Ok(devices);
    }

    [HttpGet("status/{status}")]
    [ProducesResponseType(typeof(IEnumerable<IoTDevice>), 200)]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetByStatus(DeviceStatus status, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var devices = await _deviceService.GetByStatusAsync(status, scope.TenantId);
        return Ok(devices);
    }

    [HttpGet("streaming")]
    [ProducesResponseType(typeof(IEnumerable<IoTDevice>), 200)]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetStreamingDevices(CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var devices = await _deviceService.GetStreamingDevicesAsync(scope.TenantId);
        return Ok(devices);
    }

    [HttpPost]
    [ProducesResponseType(typeof(IoTDevice), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<IoTDevice>> Create([FromBody] IoTDeviceRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        // The room has to belong to the same tenant, or a device could be placed in another tenant's room.
        if (!await _roomService.ExistsAsync(request.RoomId, scope.TenantId))
            return BadRequest(new { error = "Unknown room." });

        var device = new IoTDevice
        {
            Name = request.Name,
            DeviceId = request.DeviceId,
            Type = request.Type,
            Manufacturer = request.Manufacturer,
            Model = request.Model,
            RoomId = request.RoomId,
            SupportsStreaming = request.SupportsStreaming,
            StreamUrl = request.StreamUrl,
            StreamProtocol = request.StreamProtocol,
            IpAddress = request.IpAddress,
            Port = request.Port,
            Capabilities = request.Capabilities,
            Metadata = request.Metadata,
            TenantId = scope.TenantId
        };

        var created = await _deviceService.CreateAsync(device);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(IoTDevice), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IoTDevice>> Update(Guid id, [FromBody] IoTDeviceRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var device = await _deviceService.GetByIdAsync(id, scope.TenantId);
        if (device == null)
            return NotFound();

        if (!await _roomService.ExistsAsync(request.RoomId, scope.TenantId))
            return BadRequest(new { error = "Unknown room." });

        // Status, last-seen times, tenant and the active flag are not part of the input; status has its own endpoint.
        device.Name = request.Name;
        device.DeviceId = request.DeviceId;
        device.Type = request.Type;
        device.Manufacturer = request.Manufacturer;
        device.Model = request.Model;
        device.RoomId = request.RoomId;
        device.SupportsStreaming = request.SupportsStreaming;
        device.StreamUrl = request.StreamUrl;
        device.StreamProtocol = request.StreamProtocol;
        device.IpAddress = request.IpAddress;
        device.Port = request.Port;
        device.Capabilities = request.Capabilities;
        device.Metadata = request.Metadata;

        var updated = await _deviceService.UpdateAsync(device);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _deviceService.DeleteAsync(id, scope.TenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    [HttpPatch("{id}/status")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] DeviceStatus status, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _deviceService.UpdateStatusAsync(id, status, scope.TenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    [HttpPost("{id}/heartbeat")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RecordHeartbeat(Guid id, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _deviceService.RecordHeartbeatAsync(id, scope.TenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Get access logs for a device (requires View permission)
    /// </summary>
    [HttpGet("{id}/logs")]
    [ProducesResponseType(typeof(IEnumerable<DeviceAccessLog>), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IEnumerable<DeviceAccessLog>>> GetAccessLogs(
        Guid id,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var tenantId = scope.TenantId;
        var userId = GetUserId();

        // Check if user has View permission on this device
        var hasPermission = await _permissionService.HasPermissionAsync(
            userId, ResourceType.IoTDevice, id, PermissionAction.View, tenantId);

        if (!hasPermission)
            return Forbid();

        var logs = await _deviceService.GetAccessLogsAsync(id, tenantId, from, to);
        return Ok(logs);
    }

    /// <summary>
    /// Request device streaming (requires Stream permission)
    /// </summary>
    [HttpPost("{id}/stream")]
    [ProducesResponseType(typeof(IoTDevice), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IoTDevice>> RequestStream(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var tenantId = scope.TenantId;
        var userId = GetUserId();

        // Check if user has Stream permission on this device
        var hasPermission = await _permissionService.HasPermissionAsync(
            userId, ResourceType.IoTDevice, id, PermissionAction.Stream, tenantId);

        if (!hasPermission)
            return Forbid();

        var device = await _deviceService.GetByIdAsync(id, tenantId);
        if (device == null)
            return NotFound();

        if (!device.SupportsStreaming)
            return BadRequest("Device does not support streaming");

        // Log the access
        await _deviceService.LogAccessAsync(new DeviceAccessLog
        {
            DeviceId = id,
            UserId = userId,
            Action = DeviceAccessAction.Stream,
            Success = true,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = HttpContext.Request.Headers.UserAgent.ToString()
        });

        return Ok(device);
    }

    /// <summary>
    /// Control device (requires Control permission)
    /// </summary>
    [HttpPost("{id}/control")]
    [ProducesResponseType(204)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> ControlDevice(Guid id, [FromBody] object command, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var tenantId = scope.TenantId;
        var userId = GetUserId();

        // Check if user has Control permission on this device
        var hasPermission = await _permissionService.HasPermissionAsync(
            userId, ResourceType.IoTDevice, id, PermissionAction.Control, tenantId);

        if (!hasPermission)
            return Forbid();

        var exists = await _deviceService.ExistsAsync(id, tenantId);
        if (!exists)
            return NotFound();

        // Log the access
        await _deviceService.LogAccessAsync(new DeviceAccessLog
        {
            DeviceId = id,
            UserId = userId,
            Action = DeviceAccessAction.Control,
            Success = true,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = HttpContext.Request.Headers.UserAgent.ToString(),
            Context = System.Text.Json.JsonSerializer.Serialize(command)
        });

        // TODO: Implement actual device control logic here

        return NoContent();
    }
}
