using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

// Task 5164: authorization and tenant resolution live in BuildingManagementControllerBase.
[Route("api/[controller]")]
public class BuildingController : BuildingManagementControllerBase
{
    private readonly IBuildingService _buildingService;
    private readonly ILocationService _locationService;

    public BuildingController(IBuildingService buildingService, ILocationService locationService, ITenantAccessResolver access)
        : base(access)
    {
        _buildingService = buildingService;
        _locationService = locationService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Building>), 200)]
    public async Task<ActionResult<IEnumerable<Building>>> GetAll([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var buildings = await _buildingService.GetAllAsync(scope.TenantId, includeInactive);
        return Ok(buildings);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Building), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Building>> GetById(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var building = await _buildingService.GetByIdAsync(id, scope.TenantId);

        if (building == null)
            return NotFound();

        return Ok(building);
    }

    [HttpGet("location/{locationId}")]
    [ProducesResponseType(typeof(IEnumerable<Building>), 200)]
    public async Task<ActionResult<IEnumerable<Building>>> GetByLocation(Guid locationId, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var buildings = await _buildingService.GetByLocationAsync(locationId, scope.TenantId);
        return Ok(buildings);
    }

    [HttpPost]
    [ProducesResponseType(typeof(Building), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<Building>> Create([FromBody] BuildingRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        // The parent has to belong to the same tenant, or a building could be hung under another tenant's location.
        if (!await _locationService.ExistsAsync(request.LocationId, scope.TenantId))
            return BadRequest(new { error = "Unknown location." });

        var building = new Building
        {
            Name = request.Name,
            Code = request.Code,
            LocationId = request.LocationId,
            TotalFloors = request.TotalFloors,
            TenantId = scope.TenantId
        };

        var created = await _buildingService.CreateAsync(building);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Building), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Building>> Update(Guid id, [FromBody] BuildingRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var building = await _buildingService.GetByIdAsync(id, scope.TenantId);
        if (building == null)
            return NotFound();

        if (!await _locationService.ExistsAsync(request.LocationId, scope.TenantId))
            return BadRequest(new { error = "Unknown location." });

        building.Name = request.Name;
        building.Code = request.Code;
        building.LocationId = request.LocationId;
        building.TotalFloors = request.TotalFloors;

        var updated = await _buildingService.UpdateAsync(building);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _buildingService.DeleteAsync(id, scope.TenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    [HttpGet("{id}/floors")]
    [ProducesResponseType(typeof(IEnumerable<Floor>), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IEnumerable<Floor>>> GetFloors(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var exists = await _buildingService.ExistsAsync(id, scope.TenantId);

        if (!exists)
            return NotFound();

        var floors = await _buildingService.GetFloorsAsync(id, scope.TenantId);
        return Ok(floors);
    }
}
