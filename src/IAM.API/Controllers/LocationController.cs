using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

// Task 5164: authorization and tenant resolution live in BuildingManagementControllerBase.
[Route("api/[controller]")]
public class LocationController : BuildingManagementControllerBase
{
    private readonly ILocationService _locationService;

    public LocationController(ILocationService locationService, ITenantAccessResolver access) : base(access)
    {
        _locationService = locationService;
    }

    /// <summary>
    /// Get all locations for the tenant
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Location>), 200)]
    public async Task<ActionResult<IEnumerable<Location>>> GetAll([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var locations = await _locationService.GetAllAsync(scope.TenantId, includeInactive);
        return Ok(locations);
    }

    /// <summary>
    /// Get location by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Location), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Location>> GetById(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var location = await _locationService.GetByIdAsync(id, scope.TenantId);

        if (location == null)
            return NotFound();

        return Ok(location);
    }

    /// <summary>
    /// Create new location
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Location), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<Location>> Create([FromBody] LocationRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var location = new Location
        {
            Name = request.Name,
            Address = request.Address,
            City = request.City,
            Country = request.Country,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            TenantId = scope.TenantId
        };

        var created = await _locationService.CreateAsync(location);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update location
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Location), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Location>> Update(Guid id, [FromBody] LocationRequest request, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var location = await _locationService.GetByIdAsync(id, scope.TenantId);
        if (location == null)
            return NotFound();

        location.Name = request.Name;
        location.Address = request.Address;
        location.City = request.City;
        location.Country = request.Country;
        location.Latitude = request.Latitude;
        location.Longitude = request.Longitude;

        var updated = await _locationService.UpdateAsync(location);
        return Ok(updated);
    }

    /// <summary>
    /// Delete location (soft delete)
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var scope = await ManageScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var result = await _locationService.DeleteAsync(id, scope.TenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Get all buildings for a location
    /// </summary>
    [HttpGet("{id}/buildings")]
    [ProducesResponseType(typeof(IEnumerable<Building>), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IEnumerable<Building>>> GetBuildings(Guid id, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (scope.Failure != null) return scope.Failure;

        var exists = await _locationService.ExistsAsync(id, scope.TenantId);

        if (!exists)
            return NotFound();

        var buildings = await _locationService.GetBuildingsAsync(id, scope.TenantId);
        return Ok(buildings);
    }
}
