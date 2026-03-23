using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class LocationController : ControllerBase
{
    private readonly ILocationService _locationService;

    public LocationController(ILocationService locationService)
    {
        _locationService = locationService;
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

    /// <summary>
    /// Get all locations for the tenant
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Location>), 200)]
    public async Task<ActionResult<IEnumerable<Location>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var tenantId = GetTenantId();
        var locations = await _locationService.GetAllAsync(tenantId, includeInactive);
        return Ok(locations);
    }

    /// <summary>
    /// Get location by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Location), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Location>> GetById(Guid id)
    {
        var tenantId = GetTenantId();
        var location = await _locationService.GetByIdAsync(id, tenantId);

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
    public async Task<ActionResult<Location>> Create([FromBody] Location location)
    {
        var tenantId = GetTenantId();
        location.TenantId = tenantId;

        var created = await _locationService.CreateAsync(location);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update location
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Location), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Location>> Update(Guid id, [FromBody] Location location)
    {
        var tenantId = GetTenantId();

        if (id != location.Id)
            return BadRequest("ID mismatch");

        var exists = await _locationService.ExistsAsync(id, tenantId);
        if (!exists)
            return NotFound();

        location.TenantId = tenantId;
        var updated = await _locationService.UpdateAsync(location);
        return Ok(updated);
    }

    /// <summary>
    /// Delete location (soft delete)
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var tenantId = GetTenantId();
        var result = await _locationService.DeleteAsync(id, tenantId);

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
    public async Task<ActionResult<IEnumerable<Building>>> GetBuildings(Guid id)
    {
        var tenantId = GetTenantId();
        var exists = await _locationService.ExistsAsync(id, tenantId);

        if (!exists)
            return NotFound();

        var buildings = await _locationService.GetBuildingsAsync(id, tenantId);
        return Ok(buildings);
    }
}
