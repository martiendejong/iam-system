using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

// Task 5164: authorization and tenant resolution live in BuildingManagementControllerBase.
[Route("api/[controller]")]
public class RoomGroupController : BuildingManagementControllerBase
{
    private readonly IRoomGroupService _roomGroupService;
    private readonly IFloorService _floorService;
    private readonly IBuildingService _buildingService;

    public RoomGroupController(
        IRoomGroupService roomGroupService,
        IFloorService floorService,
        IBuildingService buildingService,
        ITenantAccessResolver access) : base(access)
    {
        _roomGroupService = roomGroupService;
        _floorService = floorService;
        _buildingService = buildingService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RoomGroup>>> GetAll([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var groups = await _roomGroupService.GetAllAsync(scope.TenantId, includeInactive);
        return Ok(groups);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<RoomGroup>> GetById(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var group = await _roomGroupService.GetByIdAsync(id, scope.TenantId);
        if (group == null) return NotFound();
        return Ok(group);
    }

    [HttpGet("floor/{floorId}")]
    public async Task<ActionResult<IEnumerable<RoomGroup>>> GetByFloor(Guid floorId, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var groups = await _roomGroupService.GetByFloorAsync(floorId, scope.TenantId);
        return Ok(groups);
    }

    [HttpGet("building/{buildingId}")]
    public async Task<ActionResult<IEnumerable<RoomGroup>>> GetByBuilding(Guid buildingId, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var groups = await _roomGroupService.GetByBuildingAsync(buildingId, scope.TenantId);
        return Ok(groups);
    }

    [HttpPost]
    public async Task<ActionResult<RoomGroup>> Create([FromBody] RoomGroupRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var parentError = await CheckParentsAsync(request, scope.TenantId);
        if (parentError != null) return parentError;

        var roomGroup = new RoomGroup
        {
            Name = request.Name,
            Description = request.Description,
            FloorId = request.FloorId,
            BuildingId = request.BuildingId,
            TenantId = scope.TenantId
        };

        var created = await _roomGroupService.CreateAsync(roomGroup);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<RoomGroup>> Update(Guid id, [FromBody] RoomGroupRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var roomGroup = await _roomGroupService.GetByIdAsync(id, scope.TenantId);
        if (roomGroup == null) return NotFound();

        var parentError = await CheckParentsAsync(request, scope.TenantId);
        if (parentError != null) return parentError;

        roomGroup.Name = request.Name;
        roomGroup.Description = request.Description;
        roomGroup.FloorId = request.FloorId;
        roomGroup.BuildingId = request.BuildingId;

        var updated = await _roomGroupService.UpdateAsync(roomGroup);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _roomGroupService.DeleteAsync(id, scope.TenantId);
        if (!result) return NotFound();
        return NoContent();
    }

    [HttpPost("{groupId}/rooms/{roomId}")]
    public async Task<IActionResult> AddRoom(Guid groupId, Guid roomId, [FromQuery] string? notes = null, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _roomGroupService.AddRoomAsync(groupId, roomId, scope.TenantId, notes);
        if (!result) return NotFound("Room group or room not found");
        return NoContent();
    }

    [HttpDelete("{groupId}/rooms/{roomId}")]
    public async Task<IActionResult> RemoveRoom(Guid groupId, Guid roomId, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _roomGroupService.RemoveRoomAsync(groupId, roomId, scope.TenantId);
        if (!result) return NotFound();
        return NoContent();
    }

    [HttpGet("{id}/rooms")]
    public async Task<ActionResult<IEnumerable<Room>>> GetRooms(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var rooms = await _roomGroupService.GetRoomsAsync(id, scope.TenantId);
        return Ok(rooms);
    }

    /// <summary>The optional floor and building of a group have to belong to the same tenant.</summary>
    private async Task<ActionResult?> CheckParentsAsync(RoomGroupRequest request, Guid tenantId)
    {
        if (request.FloorId.HasValue && !await _floorService.ExistsAsync(request.FloorId.Value, tenantId))
            return BadRequest(new { error = "Unknown floor." });

        if (request.BuildingId.HasValue && !await _buildingService.ExistsAsync(request.BuildingId.Value, tenantId))
            return BadRequest(new { error = "Unknown building." });

        return null;
    }
}
