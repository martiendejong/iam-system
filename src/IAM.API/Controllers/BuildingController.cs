using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class BuildingController : ControllerBase
{
    private readonly IBuildingService _buildingService;

    public BuildingController(IBuildingService buildingService)
    {
        _buildingService = buildingService;
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
    [ProducesResponseType(typeof(IEnumerable<Building>), 200)]
    public async Task<ActionResult<IEnumerable<Building>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var tenantId = GetTenantId();
        var buildings = await _buildingService.GetAllAsync(tenantId, includeInactive);
        return Ok(buildings);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Building), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Building>> GetById(Guid id)
    {
        var tenantId = GetTenantId();
        var building = await _buildingService.GetByIdAsync(id, tenantId);

        if (building == null)
            return NotFound();

        return Ok(building);
    }

    [HttpGet("location/{locationId}")]
    [ProducesResponseType(typeof(IEnumerable<Building>), 200)]
    public async Task<ActionResult<IEnumerable<Building>>> GetByLocation(Guid locationId)
    {
        var tenantId = GetTenantId();
        var buildings = await _buildingService.GetByLocationAsync(locationId, tenantId);
        return Ok(buildings);
    }

    [HttpPost]
    [ProducesResponseType(typeof(Building), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<Building>> Create([FromBody] Building building)
    {
        var tenantId = GetTenantId();
        building.TenantId = tenantId;

        var created = await _buildingService.CreateAsync(building);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Building), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Building>> Update(Guid id, [FromBody] Building building)
    {
        var tenantId = GetTenantId();

        if (id != building.Id)
            return BadRequest("ID mismatch");

        var exists = await _buildingService.ExistsAsync(id, tenantId);
        if (!exists)
            return NotFound();

        building.TenantId = tenantId;
        var updated = await _buildingService.UpdateAsync(building);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var tenantId = GetTenantId();
        var result = await _buildingService.DeleteAsync(id, tenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    [HttpGet("{id}/floors")]
    [ProducesResponseType(typeof(IEnumerable<Floor>), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IEnumerable<Floor>>> GetFloors(Guid id)
    {
        var tenantId = GetTenantId();
        var exists = await _buildingService.ExistsAsync(id, tenantId);

        if (!exists)
            return NotFound();

        var floors = await _buildingService.GetFloorsAsync(id, tenantId);
        return Ok(floors);
    }
}
