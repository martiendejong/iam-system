using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RegionsController : ControllerBase
{
    private readonly IRegionService _regionService;
    private readonly ILogger<RegionsController> _logger;

    public RegionsController(IRegionService regionService, ILogger<RegionsController> logger)
    {
        _regionService = regionService;
        _logger = logger;
    }

    // ────────────────────────────────────────────────────────────
    //  Region Management
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// List all registered regions
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<RegionConfig>>> GetRegions(CancellationToken ct = default)
    {
        var regions = await _regionService.GetAllRegionsAsync(ct);
        return Ok(regions);
    }

    /// <summary>
    /// Get a region by ID
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RegionConfig>> GetRegion(Guid id, CancellationToken ct = default)
    {
        var region = await _regionService.GetRegionAsync(id, ct);
        if (region == null) return NotFound();
        return Ok(region);
    }

    /// <summary>
    /// Register a new region
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<RegionConfig>> RegisterRegion(
        [FromBody] RegisterRegionRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Region name is required");

        if (string.IsNullOrWhiteSpace(request.Endpoint))
            return BadRequest("Region endpoint is required");

        var region = new RegionConfig
        {
            Name = request.Name,
            Endpoint = request.Endpoint,
            IsPrimary = request.IsPrimary,
            Status = request.Status,
            Description = request.Description,
            Priority = request.Priority
        };

        var created = await _regionService.RegisterRegionAsync(region, ct);
        return CreatedAtAction(nameof(GetRegion), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update a region
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RegionConfig>> UpdateRegion(
        Guid id,
        [FromBody] RegisterRegionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var region = new RegionConfig
            {
                Name = request.Name,
                Endpoint = request.Endpoint,
                IsPrimary = request.IsPrimary,
                Status = request.Status,
                Description = request.Description,
                Priority = request.Priority
            };

            var updated = await _regionService.UpdateRegionAsync(id, region, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a region
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> DeleteRegion(Guid id, CancellationToken ct = default)
    {
        try
        {
            var deleted = await _regionService.DeleteRegionAsync(id, ct);
            if (!deleted) return NotFound();
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Health Checks
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Check health of a specific region
    /// </summary>
    [HttpGet("{id:guid}/health")]
    public async Task<ActionResult<RegionHealthResult>> CheckRegionHealth(Guid id, CancellationToken ct = default)
    {
        try
        {
            var result = await _regionService.CheckRegionHealthAsync(id, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Failover
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger failover to a specific region or auto-select the best candidate
    /// </summary>
    [HttpPost("failover")]
    public async Task<ActionResult<RegionConfig>> TriggerFailover(
        [FromBody] FailoverRequest? request = null,
        CancellationToken ct = default)
    {
        try
        {
            var newPrimary = await _regionService.TriggerFailoverAsync(request?.TargetRegionId, ct);
            return Ok(newPrimary);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Sync Status
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Get synchronization status summary across all regions
    /// </summary>
    [HttpGet("sync-status")]
    public async Task<ActionResult<RegionSyncSummary>> GetSyncStatus(CancellationToken ct = default)
    {
        var summary = await _regionService.GetSyncSummaryAsync(ct);
        return Ok(summary);
    }

    /// <summary>
    /// Get recent sync events
    /// </summary>
    [HttpGet("sync-events")]
    public async Task<ActionResult<List<RegionSyncEvent>>> GetSyncEvents(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var events = await _regionService.GetSyncEventsAsync(skip, take, ct);
        return Ok(events);
    }

    /// <summary>
    /// Resolve a sync conflict
    /// </summary>
    [HttpPost("sync-events/{id:guid}/resolve")]
    public async Task<ActionResult<RegionSyncEvent>> ResolveSyncConflict(
        Guid id,
        [FromBody] ResolveSyncConflictRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var resolved = await _regionService.ResolveSyncConflictAsync(id, request.Resolution, ct);
            return Ok(resolved);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

// ── Request DTOs ─────────────────────────────────────────────────

public class RegisterRegionRequest
{
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public RegionStatus Status { get; set; } = RegionStatus.Active;
    public string? Description { get; set; }
    public int Priority { get; set; }
}

public class FailoverRequest
{
    public Guid? TargetRegionId { get; set; }
}

public class ResolveSyncConflictRequest
{
    public ConflictResolution Resolution { get; set; }
}
