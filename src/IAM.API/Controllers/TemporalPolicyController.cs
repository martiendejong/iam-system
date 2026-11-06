using System.Security.Claims;
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
public class TemporalPolicyController : ControllerBase
{
    private readonly ITemporalPolicyEngine _temporalEngine;
    private readonly ITenantAccessResolver _access;
    private readonly IAMDbContext _context;
    private readonly ILogger<TemporalPolicyController> _logger;

    public TemporalPolicyController(
        ITemporalPolicyEngine temporalEngine,
        ITenantAccessResolver access,
        IAMDbContext context,
        ILogger<TemporalPolicyController> logger)
    {
        _temporalEngine = temporalEngine;
        _access = access;
        _context = context;
        _logger = logger;
    }

    // Task 5850. Every action is checked against the caller's own tenants, like DevicesController: the tenant of an
    // existing grant or policy comes from the stored row, never from the request. Granting and revoking need
    // management rights in the grant's tenant (TenantManagementAuthority) and only roles TenantRoleGrantRules lets
    // that grantor hand out. Denied calls write nothing. Device and service-account tokens get 403.

    private ObjectResult Forbidden(string message) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = message });

    private Guid? CallerUserId() =>
        Guid.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;

    /// <summary>
    /// Grant temporary access to a user
    /// </summary>
    [HttpPost("access/grant")]
    public async Task<ActionResult<TemporaryAccessGrant>> GrantTemporaryAccess(
        [FromBody] GrantTemporaryAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        var access = await _access.ResolveAsync(User, cancellationToken);
        if (access.IsRefused)
            return Forbidden("This token cannot grant temporary access.");

        var grantedBy = CallerUserId();
        if (grantedBy == null)
            return Unauthorized("User ID not found in token");

        // Authorize before looking anything up, so a caller outside the tenant learns nothing about its users/roles.
        if (!access.CanManage(request.TenantId))
            return Forbidden("Only SuperAdmin or a tenant owner/manager can grant temporary access in this tenant.");

        var grantor = await TenantManagementAuthority.ResolveAsync(
            _context, grantedBy.Value, User.IsInRole("SuperAdmin"), request.TenantId, cancellationToken);
        if (grantor == null)
            return Forbidden("Only SuperAdmin or a tenant owner/manager can grant temporary access in this tenant.");

        if (request.UserId == grantedBy.Value && !grantor.IsSuperAdmin)
            return Forbidden("You cannot grant temporary access to yourself.");

        var role = await _context.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RoleId, cancellationToken);
        if (role == null)
            return BadRequest("Role not found");

        var problem = TenantRoleGrantRules.Check(role, request.TenantId, grantor);
        if (problem != RoleGrantProblem.None)
            return Forbidden(TenantRoleGrantRules.Describe(problem, role.Name));

        try
        {
            var grant = await _temporalEngine.GrantTemporaryAccessAsync(
                request.UserId,
                request.RoleId,
                request.TenantId,
                request.StartTime,
                request.EndTime,
                request.Justification,
                grantedBy.Value,
                cancellationToken);

            return Ok(grant);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Revoke temporary access grant
    /// </summary>
    [HttpPost("access/{grantId}/revoke")]
    public async Task<ActionResult<bool>> RevokeTemporaryAccess(
        Guid grantId,
        [FromBody] RevokeAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        var access = await _access.ResolveAsync(User, cancellationToken);
        if (access.IsRefused)
            return Forbidden("This token cannot revoke temporary access.");

        var revokedBy = CallerUserId();
        if (revokedBy == null)
            return Unauthorized("User ID not found in token");

        // A caller who manages nothing gets 403 for any id instead of learning which ids exist.
        if (!access.CanManageAny)
            return Forbidden("Only SuperAdmin or a tenant owner/manager can revoke temporary access.");

        var grantTenantId = await _context.TemporaryAccessGrants.AsNoTracking()
            .Where(g => g.Id == grantId)
            .Select(g => (Guid?)g.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        // A grant of a tenant the caller cannot manage is reported exactly like an unknown one.
        if (grantTenantId == null || !access.CanManage(grantTenantId.Value))
            return NotFound($"Grant {grantId} not found or already revoked");

        var grantor = await TenantManagementAuthority.ResolveAsync(
            _context, revokedBy.Value, User.IsInRole("SuperAdmin"), grantTenantId.Value, cancellationToken);
        if (grantor == null)
            return NotFound($"Grant {grantId} not found or already revoked");

        var success = await _temporalEngine.RevokeTemporaryAccessAsync(
            grantId, revokedBy.Value, request.Reason, cancellationToken);

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
        var access = await _access.ResolveAsync(User, cancellationToken);
        if (access.IsRefused)
            return Forbidden("This token cannot read temporary access.");

        // Own grants, or those of any user in a tenant the caller manages.
        if (CallerUserId() != userId && !access.CanManage(tenantId))
            return Forbidden("You can only read your own temporary access, or that of a tenant you manage.");

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
        var access = await _access.ResolveAsync(User, cancellationToken);
        if (!access.CanRead(tenantId))
            return Forbidden("You do not have access to this tenant.");

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
        var access = await _access.ResolveAsync(User, cancellationToken);
        if (!access.CanRead(tenantId))
            return Forbidden("You do not have access to this tenant.");

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
        var access = await _access.ResolveAsync(User, cancellationToken);
        if (access.IsRefused)
            return Forbidden("This token cannot read policies.");

        // A policy of a tenant the caller cannot read is reported exactly like an unknown one.
        var policyTenantId = await _context.Policies.AsNoTracking()
            .Where(p => p.Id == policyId)
            .Select(p => (Guid?)p.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (policyTenantId == null || !access.CanRead(policyTenantId.Value))
            return NotFound($"Policy {policyId} not found");

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
