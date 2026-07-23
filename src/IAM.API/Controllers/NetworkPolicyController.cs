using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NetworkPolicyController : ControllerBase
{
    private readonly INetworkPolicyService _networkPolicyService;
    private readonly ILogger<NetworkPolicyController> _logger;

    public NetworkPolicyController(
        INetworkPolicyService networkPolicyService,
        ILogger<NetworkPolicyController> logger)
    {
        _networkPolicyService = networkPolicyService;
        _logger = logger;
    }

    #region IP Allowlist

    /// <summary>
    /// Get all IP allowlist entries for a tenant
    /// </summary>
    [HttpGet("ip-allowlist")]
    public async Task<ActionResult<List<IpAllowlistEntry>>> GetIpAllowlist(
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _networkPolicyService.GetIpAllowlistAsync(tenantId, cancellationToken);
        return Ok(entries);
    }

    /// <summary>
    /// Create a new IP allowlist entry
    /// </summary>
    [HttpPost("ip-allowlist")]
    public async Task<ActionResult<IpAllowlistEntry>> CreateIpAllowlistEntry(
        [FromBody] CreateIpAllowlistRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = await _networkPolicyService.CreateIpAllowlistEntryAsync(
                request.TenantId, request.Cidr, request.Description, cancellationToken);
            return Ok(entry);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update an IP allowlist entry
    /// </summary>
    [HttpPut("ip-allowlist/{id}")]
    public async Task<ActionResult<IpAllowlistEntry>> UpdateIpAllowlistEntry(
        Guid id,
        [FromBody] UpdateIpAllowlistRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = await _networkPolicyService.UpdateIpAllowlistEntryAsync(
                id, request.Cidr, request.Description, request.IsActive, cancellationToken);
            return Ok(entry);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete an IP allowlist entry
    /// </summary>
    [HttpDelete("ip-allowlist/{id}")]
    public async Task<ActionResult> DeleteIpAllowlistEntry(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _networkPolicyService.DeleteIpAllowlistEntryAsync(id, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    #endregion

    #region Geo Restrictions

    /// <summary>
    /// Get geo restriction for a tenant
    /// </summary>
    [HttpGet("geo-restriction")]
    public async Task<ActionResult<GeoRestriction>> GetGeoRestriction(
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var restriction = await _networkPolicyService.GetGeoRestrictionAsync(tenantId, cancellationToken);
        return Ok(restriction);
    }

    /// <summary>
    /// Create or update geo restriction for a tenant
    /// </summary>
    [HttpPut("geo-restriction")]
    public async Task<ActionResult<GeoRestriction>> UpsertGeoRestriction(
        [FromBody] UpsertGeoRestrictionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var restriction = await _networkPolicyService.UpsertGeoRestrictionAsync(
                request.TenantId, request.AllowedCountries, request.BlockedCountries, request.IsActive, cancellationToken);
            return Ok(restriction);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete geo restriction for a tenant
    /// </summary>
    [HttpDelete("geo-restriction")]
    public async Task<ActionResult> DeleteGeoRestriction(
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _networkPolicyService.DeleteGeoRestrictionAsync(tenantId, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    #endregion

    #region GeoFences

    /// <summary>
    /// Get all geofences for a tenant
    /// </summary>
    [HttpGet("geofences")]
    public async Task<ActionResult<List<GeoFence>>> GetGeoFences(
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var fences = await _networkPolicyService.GetGeoFencesAsync(tenantId, cancellationToken);
        return Ok(fences);
    }

    /// <summary>
    /// Create a new geofence
    /// </summary>
    [HttpPost("geofences")]
    public async Task<ActionResult<GeoFence>> CreateGeoFence(
        [FromBody] CreateGeoFenceRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fence = await _networkPolicyService.CreateGeoFenceAsync(
                request.TenantId, request.Name, request.Latitude, request.Longitude,
                request.RadiusMeters, request.Description, cancellationToken);
            return Ok(fence);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update a geofence
    /// </summary>
    [HttpPut("geofences/{id}")]
    public async Task<ActionResult<GeoFence>> UpdateGeoFence(
        Guid id,
        [FromBody] UpdateGeoFenceRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fence = await _networkPolicyService.UpdateGeoFenceAsync(
                id, request.Name, request.Latitude, request.Longitude,
                request.RadiusMeters, request.Description, request.IsActive, cancellationToken);
            return Ok(fence);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a geofence
    /// </summary>
    [HttpDelete("geofences/{id}")]
    public async Task<ActionResult> DeleteGeoFence(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _networkPolicyService.DeleteGeoFenceAsync(id, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    #endregion

    #region Blocked IP Log

    /// <summary>
    /// Get blocked IP log entries for a tenant
    /// </summary>
    [HttpGet("blocked-log")]
    public async Task<ActionResult<List<BlockedIpLog>>> GetBlockedLog(
        [FromQuery] Guid tenantId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var logs = await _networkPolicyService.GetBlockedIpLogsAsync(tenantId, skip, take, cancellationToken);
        return Ok(logs);
    }

    #endregion

    #region IP Check

    /// <summary>
    /// Test an IP address against all network policies for a tenant
    /// </summary>
    [HttpPost("check-ip")]
    public async Task<ActionResult<IpCheckResult>> CheckIp(
        [FromBody] CheckIpRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _networkPolicyService.CheckIpAsync(
                request.TenantId, request.IpAddress, request.Latitude, request.Longitude, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    #endregion
}

#region Request DTOs

public class CreateIpAllowlistRequest
{
    public Guid TenantId { get; set; }
    public string Cidr { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class UpdateIpAllowlistRequest
{
    public string Cidr { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UpsertGeoRestrictionRequest
{
    public Guid TenantId { get; set; }
    public string? AllowedCountries { get; set; }
    public string? BlockedCountries { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CreateGeoFenceRequest
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double RadiusMeters { get; set; }
    public string? Description { get; set; }
}

public class UpdateGeoFenceRequest
{
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double RadiusMeters { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CheckIpRequest
{
    public Guid TenantId { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

#endregion
