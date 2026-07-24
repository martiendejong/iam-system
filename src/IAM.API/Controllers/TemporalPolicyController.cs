using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TemporalPolicyController : ControllerBase
{
    private readonly ITemporalPolicyEngine _temporalEngine;
    private readonly ILogger<TemporalPolicyController> _logger;

    public TemporalPolicyController(
        ITemporalPolicyEngine temporalEngine,
        ILogger<TemporalPolicyController> logger)
    {
        _temporalEngine = temporalEngine;
        _logger = logger;
    }

    /// <summary>
    /// Grant temporary access to a user
    /// </summary>
    [HttpPost("access/grant")]
    [Authorize(Roles = "TenantAdmin,SystemAdmin")]
    public async Task<ActionResult<TemporaryAccessGrant>> GrantTemporaryAccess(
        [FromBody] GrantTemporaryAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        // Get user ID from claims (try multiple claim types)
        var userIdClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        if (!Guid.TryParse(userIdClaim, out var grantedBy))
            return Unauthorized("User ID not found in token");

        try
        {
            var grant = await _temporalEngine.GrantTemporaryAccessAsync(
                request.UserId,
                request.RoleId,
                request.TenantId,
                request.StartTime,
                request.EndTime,
                request.Justification,
                grantedBy,
                cancellationToken);

            return Ok(grant);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Revoke temporary access grant
    /// </summary>
    [HttpPost("access/{grantId}/revoke")]
    [Authorize(Roles = "TenantAdmin,SystemAdmin")]
    public async Task<ActionResult<bool>> RevokeTemporaryAccess(
        Guid grantId,
        [FromBody] RevokeAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        // Get user ID from claims (try multiple claim types)
        var userIdClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        if (!Guid.TryParse(userIdClaim, out var revokedBy))
            return Unauthorized("User ID not found in token");

        var success = await _temporalEngine.RevokeTemporaryAccessAsync(
            grantId, revokedBy, request.Reason, cancellationToken);

        if (!success)
            return NotFound($"Grant {grantId} not found or already revoked");

        return Ok(new { Success = true });
    }

    /// <summary>
    /// Get active temporary access grants for a user
    /// </summary>
    [HttpGet("access/active")]
    public async Task<ActionResult<List<TemporaryAccessGrant>>> GetActiveTemporaryAccess(
        [FromQuery] Guid userId,
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var grants = await _temporalEngine.GetActiveTemporaryAccessAsync(
            userId, tenantId, cancellationToken);

        return Ok(grants);
    }

    /// <summary>
    /// Get active maintenance windows affecting a tenant
    /// </summary>
    [HttpGet("maintenance/active")]
    public async Task<ActionResult<List<MaintenanceWindow>>> GetActiveMaintenanceWindows(
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var windows = await _temporalEngine.GetActiveMaintenanceWindowsAsync(
            tenantId, cancellationToken);

        return Ok(windows);
    }

    /// <summary>
    /// Check if tenant is under maintenance
    /// </summary>
    [HttpGet("maintenance/check")]
    public async Task<ActionResult> CheckMaintenanceStatus(
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var (isUnderMaintenance, behavior) = await _temporalEngine.CheckMaintenanceStatusAsync(
            tenantId, cancellationToken);

        return Ok(new
        {
            IsUnderMaintenance = isUnderMaintenance,
            Behavior = behavior?.ToString()
        });
    }

    /// <summary>
    /// Check if a policy is active at a given time
    /// </summary>
    [HttpGet("policies/{policyId}/active")]
    public async Task<ActionResult<bool>> IsPolicyActiveAt(
        Guid policyId,
        [FromQuery] DateTime? dateTime,
        CancellationToken cancellationToken = default)
    {
        var checkDateTime = dateTime ?? DateTime.UtcNow;
        var isActive = await _temporalEngine.IsPolicyActiveAtAsync(
            policyId, checkDateTime, cancellationToken);

        return Ok(new { IsActive = isActive, CheckedAt = checkDateTime });
    }
}

public class GrantTemporaryAccessRequest
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Justification { get; set; } = string.Empty;
}

public class RevokeAccessRequest
{
    public string Reason { get; set; } = string.Empty;
}
