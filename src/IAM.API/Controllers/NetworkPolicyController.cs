using System.Security.Claims;
using Hazina.Security.ApiKeys;
using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NetworkPolicyController : ControllerBase
{
    private readonly INetworkPolicyService _networkPolicyService;
    private readonly IAMDbContext _context;
    private readonly ILogger<NetworkPolicyController> _logger;

    public NetworkPolicyController(
        INetworkPolicyService networkPolicyService,
        IAMDbContext context,
        ILogger<NetworkPolicyController> logger)
    {
        _networkPolicyService = networkPolicyService;
        _context = context;
        _logger = logger;
    }

    // Task 5160. Network policy (IP allowlist, geo rules, geofences, blocked-IP log, IP check) exposes internal
    // ranges, office locations and blocked client IPs, and will lock tenants out once it is enforced at login, so every
    // action needs a global administrator (SuperAdmin/SystemAdmin) or an administrator of the tenant it touches
    // (active TenantAdmin/BuildingOwner/BuildingManager UserRoles row, TenantAdminAuthority). Role claims alone confer
    // nothing, a token pinned to another tenant, device / service-account tokens and API keys are refused. The check runs
    // before any lookup; for routes addressed by an entry id the tenant comes from the stored entry, never the request,
    // and a caller who may not act gets the same 403 whether the id is foreign or unknown.

    private ObjectResult Forbidden() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "Only SuperAdmin, SystemAdmin or an administrator of this tenant can manage its network policy." });

    private bool IsGlobalAdmin() => User.IsInRole("SuperAdmin") || User.IsInRole("SystemAdmin");

    private bool IsRefusedPrincipal() =>
        User.IsApiKey() || User.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null
        || User.FindFirst("token_type")?.Value is "device" or "service_account";

    /// <summary>The caller's user id, or null when the principal may not use this API at all.</summary>
    private Guid? CallerUserId()
    {
        if (IsRefusedPrincipal())
            return null;

        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(subject, out var id) ? id : null;
    }

    /// <summary>Null when the caller may act on the tenant, otherwise the 403 to send.</summary>
    private async Task<ActionResult?> RequireTenantAdminAsync(Guid tenantId, CancellationToken ct)
    {
        if (IsRefusedPrincipal())
            return Forbidden();

        if (IsGlobalAdmin())
            return null;

        if (CallerUserId() is not { } userId)
            return Forbidden();

        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim)
            && (!Guid.TryParse(tenantClaim, out var pinned) || pinned != tenantId))
            return Forbidden();

        return await TenantAdminAuthority.HasAdministratorRoleInTenantAsync(_context, userId, tenantId, ct)
            ? null
            : Forbidden();
    }

    /// <summary>
    /// Authorization for a route addressed by an entry id: the coarse gate first (no lookup for a caller who administers
    /// nothing), then the stored tenant of the entry. <paramref name="storedTenant"/> returns null when there is no such entry.
    /// </summary>
    private async Task<ActionResult?> RequireAdminOfEntryAsync(Func<Task<Guid?>> storedTenant, CancellationToken ct)
    {
        if (IsRefusedPrincipal())
            return Forbidden();

        if (!IsGlobalAdmin())
        {
            if (CallerUserId() is not { } userId
                || !await TenantAdminAuthority.HasAdministratorRoleInAnyTenantAsync(_context, userId, ct))
                return Forbidden();
        }

        var tenantId = await storedTenant();
        if (tenantId == null)
            return IsGlobalAdmin() ? NotFound(new { error = "Entry not found" }) : Forbidden();

        return await RequireTenantAdminAsync(tenantId.Value, ct);
    }

    private Task<Guid?> StoredAllowlistTenantAsync(Guid id, CancellationToken ct) =>
        _context.IpAllowlistEntries.AsNoTracking().Where(e => e.Id == id).Select(e => (Guid?)e.TenantId).FirstOrDefaultAsync(ct);

    private Task<Guid?> StoredGeoFenceTenantAsync(Guid id, CancellationToken ct) =>
        _context.GeoFences.AsNoTracking().Where(e => e.Id == id).Select(e => (Guid?)e.TenantId).FirstOrDefaultAsync(ct);

    #region IP Allowlist

    /// <summary>
    /// Get all IP allowlist entries for a tenant
    /// </summary>
    [HttpGet("ip-allowlist")]
    public async Task<ActionResult<List<IpAllowlistEntry>>> GetIpAllowlist(
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (await RequireTenantAdminAsync(tenantId, cancellationToken) is { } denied)
            return denied;

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
        if (await RequireTenantAdminAsync(request.TenantId, cancellationToken) is { } denied)
            return denied;

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
        if (await RequireAdminOfEntryAsync(() => StoredAllowlistTenantAsync(id, cancellationToken), cancellationToken) is { } denied)
            return denied;

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
        if (await RequireAdminOfEntryAsync(() => StoredAllowlistTenantAsync(id, cancellationToken), cancellationToken) is { } denied)
            return denied;

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
        if (await RequireTenantAdminAsync(tenantId, cancellationToken) is { } denied)
            return denied;

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
        if (await RequireTenantAdminAsync(request.TenantId, cancellationToken) is { } denied)
            return denied;

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
        if (await RequireTenantAdminAsync(tenantId, cancellationToken) is { } denied)
            return denied;

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
        if (await RequireTenantAdminAsync(tenantId, cancellationToken) is { } denied)
            return denied;

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
        if (await RequireTenantAdminAsync(request.TenantId, cancellationToken) is { } denied)
            return denied;

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
        if (await RequireAdminOfEntryAsync(() => StoredGeoFenceTenantAsync(id, cancellationToken), cancellationToken) is { } denied)
            return denied;

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
        if (await RequireAdminOfEntryAsync(() => StoredGeoFenceTenantAsync(id, cancellationToken), cancellationToken) is { } denied)
            return denied;

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
        if (await RequireTenantAdminAsync(tenantId, cancellationToken) is { } denied)
            return denied;

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
        if (await RequireTenantAdminAsync(request.TenantId, cancellationToken) is { } denied)
            return denied;

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
