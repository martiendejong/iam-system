using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class FloorController : ControllerBase
{
    private readonly IFloorService _floorService;

    public FloorController(IFloorService floorService)
    {
        _floorService = floorService;
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
    public async Task<ActionResult<IEnumerable<Floor>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var tenantId = GetTenantId();
        var floors = await _floorService.GetAllAsync(tenantId, includeInactive);
        return Ok(floors);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Floor>> GetById(Guid id)
    {
        var tenantId = GetTenantId();
        var floor = await _floorService.GetByIdAsync(id, tenantId);
        if (floor == null) return NotFound();
        return Ok(floor);
    }

    [HttpGet("building/{buildingId}")]
    public async Task<ActionResult<IEnumerable<Floor>>> GetByBuilding(Guid buildingId)
    {
        var tenantId = GetTenantId();
        var floors = await _floorService.GetByBuildingAsync(buildingId, tenantId);
        return Ok(floors);
    }

    [HttpPost]
    public async Task<ActionResult<Floor>> Create([FromBody] Floor floor)
    {
        var tenantId = GetTenantId();
        floor.TenantId = tenantId;
        var created = await _floorService.CreateAsync(floor);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Floor>> Update(Guid id, [FromBody] Floor floor)
    {
        var tenantId = GetTenantId();
        if (id != floor.Id) return BadRequest("ID mismatch");
        var exists = await _floorService.ExistsAsync(id, tenantId);
        if (!exists) return NotFound();
        floor.TenantId = tenantId;
        var updated = await _floorService.UpdateAsync(floor);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var tenantId = GetTenantId();
        var result = await _floorService.DeleteAsync(id, tenantId);
        if (!result) return NotFound();
        return NoContent();
    }

    [HttpGet("{id}/rooms")]
    public async Task<ActionResult<IEnumerable<Room>>> GetRooms(Guid id)
    {
        var tenantId = GetTenantId();
        var exists = await _floorService.ExistsAsync(id, tenantId);
        if (!exists) return NotFound();
        var rooms = await _floorService.GetRoomsAsync(id, tenantId);
        return Ok(rooms);
    }
}
