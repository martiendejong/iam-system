using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class IoTDeviceController : ControllerBase
{
    private readonly IIoTDeviceService _deviceService;
    private readonly IResourcePermissionService _permissionService;

    public IoTDeviceController(IIoTDeviceService deviceService, IResourcePermissionService permissionService)
    {
        _deviceService = deviceService;
        _permissionService = permissionService;
    }

    private Guid GetTenantId()
    {
        var tenantIdClaim = User.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrEmpty(tenantIdClaim) || !Guid.TryParse(tenantIdClaim, out var tenantId))
        {
            throw new UnauthorizedAccessException("Tenant ID not found in token");
        }
        return tenantId;
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
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var tenantId = GetTenantId();
        var devices = await _deviceService.GetAllAsync(tenantId, includeInactive);
        return Ok(devices);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(IoTDevice), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IoTDevice>> GetById(Guid id)
    {
        var tenantId = GetTenantId();
        var device = await _deviceService.GetByIdAsync(id, tenantId);

        if (device == null)
            return NotFound();

        return Ok(device);
    }

    [HttpGet("device-id/{deviceId}")]
    [ProducesResponseType(typeof(IoTDevice), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IoTDevice>> GetByDeviceId(string deviceId)
    {
        var tenantId = GetTenantId();
        var device = await _deviceService.GetByDeviceIdAsync(deviceId, tenantId);

        if (device == null)
            return NotFound();

        return Ok(device);
    }

    [HttpGet("room/{roomId}")]
    [ProducesResponseType(typeof(IEnumerable<IoTDevice>), 200)]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetByRoom(Guid roomId)
    {
        var tenantId = GetTenantId();
        var devices = await _deviceService.GetByRoomAsync(roomId, tenantId);
        return Ok(devices);
    }

    [HttpGet("type/{type}")]
    [ProducesResponseType(typeof(IEnumerable<IoTDevice>), 200)]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetByType(DeviceType type)
    {
        var tenantId = GetTenantId();
        var devices = await _deviceService.GetByTypeAsync(type, tenantId);
        return Ok(devices);
    }

    [HttpGet("status/{status}")]
    [ProducesResponseType(typeof(IEnumerable<IoTDevice>), 200)]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetByStatus(DeviceStatus status)
    {
        var tenantId = GetTenantId();
        var devices = await _deviceService.GetByStatusAsync(status, tenantId);
        return Ok(devices);
    }

    [HttpGet("streaming")]
    [ProducesResponseType(typeof(IEnumerable<IoTDevice>), 200)]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetStreamingDevices()
    {
        var tenantId = GetTenantId();
        var devices = await _deviceService.GetStreamingDevicesAsync(tenantId);
        return Ok(devices);
    }

    [HttpPost]
    [ProducesResponseType(typeof(IoTDevice), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<IoTDevice>> Create([FromBody] IoTDevice device)
    {
        var tenantId = GetTenantId();
        device.TenantId = tenantId;

        var created = await _deviceService.CreateAsync(device);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(IoTDevice), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IoTDevice>> Update(Guid id, [FromBody] IoTDevice device)
    {
        var tenantId = GetTenantId();

        if (id != device.Id)
            return BadRequest("ID mismatch");

        var exists = await _deviceService.ExistsAsync(id, tenantId);
        if (!exists)
            return NotFound();

        device.TenantId = tenantId;
        var updated = await _deviceService.UpdateAsync(device);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var tenantId = GetTenantId();
        var result = await _deviceService.DeleteAsync(id, tenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    [HttpPatch("{id}/status")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] DeviceStatus status)
    {
        var tenantId = GetTenantId();
        var result = await _deviceService.UpdateStatusAsync(id, status, tenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    [HttpPost("{id}/heartbeat")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RecordHeartbeat(Guid id)
    {
        var tenantId = GetTenantId();
        var result = await _deviceService.RecordHeartbeatAsync(id, tenantId);

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
        [FromQuery] DateTime? to = null)
    {
        var tenantId = GetTenantId();
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
    public async Task<ActionResult<IoTDevice>> RequestStream(Guid id)
    {
        var tenantId = GetTenantId();
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
    public async Task<IActionResult> ControlDevice(Guid id, [FromBody] object command)
    {
        var tenantId = GetTenantId();
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
