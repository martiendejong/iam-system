using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class RoomController : ControllerBase
{
    private readonly IRoomService _roomService;

    public RoomController(IRoomService roomService)
    {
        _roomService = roomService;
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

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Room>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var tenantId = GetTenantId();
        var rooms = await _roomService.GetAllAsync(tenantId, includeInactive);
        return Ok(rooms);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Room>> GetById(Guid id)
    {
        var tenantId = GetTenantId();
        var room = await _roomService.GetByIdAsync(id, tenantId);
        if (room == null) return NotFound();
        return Ok(room);
    }

    [HttpGet("floor/{floorId}")]
    public async Task<ActionResult<IEnumerable<Room>>> GetByFloor(Guid floorId)
    {
        var tenantId = GetTenantId();
        var rooms = await _roomService.GetByFloorAsync(floorId, tenantId);
        return Ok(rooms);
    }

    [HttpGet("type/{type}")]
    public async Task<ActionResult<IEnumerable<Room>>> GetByType(RoomType type)
    {
        var tenantId = GetTenantId();
        var rooms = await _roomService.GetByTypeAsync(type, tenantId);
        return Ok(rooms);
    }

    [HttpPost]
    public async Task<ActionResult<Room>> Create([FromBody] Room room)
    {
        var tenantId = GetTenantId();
        room.TenantId = tenantId;
        var created = await _roomService.CreateAsync(room);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Room>> Update(Guid id, [FromBody] Room room)
    {
        var tenantId = GetTenantId();
        if (id != room.Id) return BadRequest("ID mismatch");
        var exists = await _roomService.ExistsAsync(id, tenantId);
        if (!exists) return NotFound();
        room.TenantId = tenantId;
        var updated = await _roomService.UpdateAsync(room);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var tenantId = GetTenantId();
        var result = await _roomService.DeleteAsync(id, tenantId);
        if (!result) return NotFound();
        return NoContent();
    }

    [HttpGet("{id}/devices")]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetDevices(Guid id)
    {
        var tenantId = GetTenantId();
        var exists = await _roomService.ExistsAsync(id, tenantId);
        if (!exists) return NotFound();
        var devices = await _roomService.GetDevicesAsync(id, tenantId);
        return Ok(devices);
    }

    [HttpGet("{id}/groups")]
    public async Task<ActionResult<IEnumerable<RoomGroup>>> GetGroups(Guid id)
    {
        var tenantId = GetTenantId();
        var exists = await _roomService.ExistsAsync(id, tenantId);
        if (!exists) return NotFound();
        var groups = await _roomService.GetGroupsAsync(id, tenantId);
        return Ok(groups);
    }
}
