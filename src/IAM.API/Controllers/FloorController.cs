using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

// Task 5164: authorization and tenant resolution live in BuildingManagementControllerBase.
[Route("api/[controller]")]
public class FloorController : BuildingManagementControllerBase
{
    private readonly IFloorService _floorService;
    private readonly IBuildingService _buildingService;

    public FloorController(IFloorService floorService, IBuildingService buildingService, ITenantAccessResolver access)
        : base(access)
    {
        _floorService = floorService;
        _buildingService = buildingService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Floor>>> GetAll([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var floors = await _floorService.GetAllAsync(scope.TenantId, includeInactive);
        return Ok(floors);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Floor>> GetById(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var floor = await _floorService.GetByIdAsync(id, scope.TenantId);
        if (floor == null) return NotFound();
        return Ok(floor);
    }

    [HttpGet("building/{buildingId}")]
    public async Task<ActionResult<IEnumerable<Floor>>> GetByBuilding(Guid buildingId, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var floors = await _floorService.GetByBuildingAsync(buildingId, scope.TenantId);
        return Ok(floors);
    }

    [HttpPost]
    public async Task<ActionResult<Floor>> Create([FromBody] FloorRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        if (!await _buildingService.ExistsAsync(request.BuildingId, scope.TenantId))
            return BadRequest(new { error = "Unknown building." });

        var floor = new Floor
        {
            Name = request.Name,
            FloorNumber = request.FloorNumber,
            BuildingId = request.BuildingId,
            AreaSquareMeters = request.AreaSquareMeters,
            TenantId = scope.TenantId
        };

        var created = await _floorService.CreateAsync(floor);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Floor>> Update(Guid id, [FromBody] FloorRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var floor = await _floorService.GetByIdAsync(id, scope.TenantId);
        if (floor == null) return NotFound();

        if (!await _buildingService.ExistsAsync(request.BuildingId, scope.TenantId))
            return BadRequest(new { error = "Unknown building." });

        floor.Name = request.Name;
        floor.FloorNumber = request.FloorNumber;
        floor.BuildingId = request.BuildingId;
        floor.AreaSquareMeters = request.AreaSquareMeters;

        var updated = await _floorService.UpdateAsync(floor);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _floorService.DeleteAsync(id, scope.TenantId);
        if (!result) return NotFound();
        return NoContent();
    }

    [HttpGet("{id}/rooms")]
    public async Task<ActionResult<IEnumerable<Room>>> GetRooms(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var exists = await _floorService.ExistsAsync(id, scope.TenantId);
        if (!exists) return NotFound();
        var rooms = await _floorService.GetRoomsAsync(id, scope.TenantId);
        return Ok(rooms);
    }
}
