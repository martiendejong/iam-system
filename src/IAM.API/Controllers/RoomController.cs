using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

// Task 5164: authorization and tenant resolution live in BuildingManagementControllerBase.
[Route("api/[controller]")]
public class RoomController : BuildingManagementControllerBase
{
    private readonly IRoomService _roomService;
    private readonly IFloorService _floorService;

    public RoomController(IRoomService roomService, IFloorService floorService, ITenantAccessResolver access)
        : base(access)
    {
        _roomService = roomService;
        _floorService = floorService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Room>>> GetAll([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var rooms = await _roomService.GetAllAsync(scope.TenantId, includeInactive);
        return Ok(rooms);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Room>> GetById(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var room = await _roomService.GetByIdAsync(id, scope.TenantId);
        if (room == null) return NotFound();
        return Ok(room);
    }

    [HttpGet("floor/{floorId}")]
    public async Task<ActionResult<IEnumerable<Room>>> GetByFloor(Guid floorId, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var rooms = await _roomService.GetByFloorAsync(floorId, scope.TenantId);
        return Ok(rooms);
    }

    [HttpGet("type/{type}")]
    public async Task<ActionResult<IEnumerable<Room>>> GetByType(RoomType type, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var rooms = await _roomService.GetByTypeAsync(type, scope.TenantId);
        return Ok(rooms);
    }

    [HttpPost]
    public async Task<ActionResult<Room>> Create([FromBody] RoomRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        if (!await _floorService.ExistsAsync(request.FloorId, scope.TenantId))
            return BadRequest(new { error = "Unknown floor." });

        var room = new Room
        {
            Name = request.Name,
            RoomNumber = request.RoomNumber,
            Type = request.Type,
            FloorId = request.FloorId,
            AreaSquareMeters = request.AreaSquareMeters,
            Capacity = request.Capacity,
            TenantId = scope.TenantId
        };

        var created = await _roomService.CreateAsync(room);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Room>> Update(Guid id, [FromBody] RoomRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var room = await _roomService.GetByIdAsync(id, scope.TenantId);
        if (room == null) return NotFound();

        if (!await _floorService.ExistsAsync(request.FloorId, scope.TenantId))
            return BadRequest(new { error = "Unknown floor." });

        room.Name = request.Name;
        room.RoomNumber = request.RoomNumber;
        room.Type = request.Type;
        room.FloorId = request.FloorId;
        room.AreaSquareMeters = request.AreaSquareMeters;
        room.Capacity = request.Capacity;

        var updated = await _roomService.UpdateAsync(room);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _roomService.DeleteAsync(id, scope.TenantId);
        if (!result) return NotFound();
        return NoContent();
    }

    [HttpGet("{id}/devices")]
    public async Task<ActionResult<IEnumerable<IoTDevice>>> GetDevices(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var exists = await _roomService.ExistsAsync(id, scope.TenantId);
        if (!exists) return NotFound();
        var devices = await _roomService.GetDevicesAsync(id, scope.TenantId);
        return Ok(devices);
    }

    [HttpGet("{id}/groups")]
    public async Task<ActionResult<IEnumerable<RoomGroup>>> GetGroups(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var exists = await _roomService.ExistsAsync(id, scope.TenantId);
        if (!exists) return NotFound();
        var groups = await _roomService.GetGroupsAsync(id, scope.TenantId);
        return Ok(groups);
    }
}
