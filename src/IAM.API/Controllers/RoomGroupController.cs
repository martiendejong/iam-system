using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class RoomGroupController : ControllerBase
{
    private readonly IRoomGroupService _roomGroupService;

    public RoomGroupController(IRoomGroupService roomGroupService)
    {
        _roomGroupService = roomGroupService;
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
    public async Task<ActionResult<IEnumerable<RoomGroup>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var tenantId = GetTenantId();
        var groups = await _roomGroupService.GetAllAsync(tenantId, includeInactive);
        return Ok(groups);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<RoomGroup>> GetById(Guid id)
    {
        var tenantId = GetTenantId();
        var group = await _roomGroupService.GetByIdAsync(id, tenantId);
        if (group == null) return NotFound();
        return Ok(group);
    }

    [HttpGet("floor/{floorId}")]
    public async Task<ActionResult<IEnumerable<RoomGroup>>> GetByFloor(Guid floorId)
    {
        var tenantId = GetTenantId();
        var groups = await _roomGroupService.GetByFloorAsync(floorId, tenantId);
        return Ok(groups);
    }

    [HttpGet("building/{buildingId}")]
    public async Task<ActionResult<IEnumerable<RoomGroup>>> GetByBuilding(Guid buildingId)
    {
        var tenantId = GetTenantId();
        var groups = await _roomGroupService.GetByBuildingAsync(buildingId, tenantId);
        return Ok(groups);
    }

    [HttpPost]
    public async Task<ActionResult<RoomGroup>> Create([FromBody] RoomGroup roomGroup)
    {
        var tenantId = GetTenantId();
        roomGroup.TenantId = tenantId;
        var created = await _roomGroupService.CreateAsync(roomGroup);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<RoomGroup>> Update(Guid id, [FromBody] RoomGroup roomGroup)
    {
        var tenantId = GetTenantId();
        if (id != roomGroup.Id) return BadRequest("ID mismatch");
        var exists = await _roomGroupService.ExistsAsync(id, tenantId);
        if (!exists) return NotFound();
        roomGroup.TenantId = tenantId;
        var updated = await _roomGroupService.UpdateAsync(roomGroup);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var tenantId = GetTenantId();
        var result = await _roomGroupService.DeleteAsync(id, tenantId);
        if (!result) return NotFound();
        return NoContent();
    }

    [HttpPost("{groupId}/rooms/{roomId}")]
    public async Task<IActionResult> AddRoom(Guid groupId, Guid roomId, [FromQuery] string? notes = null)
    {
        var tenantId = GetTenantId();
        var result = await _roomGroupService.AddRoomAsync(groupId, roomId, tenantId, notes);
        if (!result) return NotFound("Room group or room not found");
        return NoContent();
    }

    [HttpDelete("{groupId}/rooms/{roomId}")]
    public async Task<IActionResult> RemoveRoom(Guid groupId, Guid roomId)
    {
        var tenantId = GetTenantId();
        var result = await _roomGroupService.RemoveRoomAsync(groupId, roomId, tenantId);
        if (!result) return NotFound();
        return NoContent();
    }

    [HttpGet("{id}/rooms")]
    public async Task<ActionResult<IEnumerable<Room>>> GetRooms(Guid id)
    {
        var tenantId = GetTenantId();
        var rooms = await _roomGroupService.GetRoomsAsync(id, tenantId);
        return Ok(rooms);
    }
}
