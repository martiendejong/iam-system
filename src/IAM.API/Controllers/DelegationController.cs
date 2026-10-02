using System.Security.Claims;
using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/delegations")]
[Authorize]
public class DelegationController : ControllerBase
{
    /// <summary>Roles that may create, change or delete SoD constraints (task 4712).</summary>
    private const string SodAdminRoles = "SuperAdmin,SystemAdmin";

    private readonly IDelegationService _delegationService;
    private readonly ILogger<DelegationController> _logger;

    public DelegationController(
        IDelegationService delegationService,
        ILogger<DelegationController> logger)
    {
        _delegationService = delegationService;
        _logger = logger;
    }

    // ─── Delegations ─────────────────────────────────────────

    /// <summary>
    /// Create a new delegation.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateDelegation([FromBody] CreateDelegationDto dto, CancellationToken ct)
    {
        var failure = TryGetActor(out var actor);
        if (failure != null)
            return failure;

        try
        {
            var delegation = await _delegationService.CreateDelegationAsync(
                actor!,
                dto.DelegatorUserId ?? actor!.UserId,
                dto.DelegateUserId,
                dto.TenantId,
                dto.Permissions,
                dto.ValidFrom,
                dto.ValidUntil,
                dto.Reason,
                dto.RequiresApproval,
                ct);

            return Ok(MapDelegationToResponse(delegation));
        }
        catch (DelegationAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get delegations for a tenant. Admins see all of them; everyone else only their own.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetDelegations([FromQuery] Guid tenantId, CancellationToken ct)
    {
        var failure = TryGetActor(out var actor);
        if (failure != null)
            return failure;

        var delegations = await _delegationService.GetDelegationsAsync(actor!, tenantId, ct);
        return Ok(delegations.Select(MapDelegationToResponse));
    }

    /// <summary>
    /// Get a specific delegation by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDelegation(Guid id, CancellationToken ct)
    {
        var failure = TryGetActor(out var actor);
        if (failure != null)
            return failure;

        try
        {
            var delegation = await _delegationService.GetDelegationAsync(actor!, id, ct);
            if (delegation == null)
                return NotFound(new { error = "Delegation not found" });

            return Ok(MapDelegationToResponse(delegation));
        }
        catch (DelegationAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
    }

    /// <summary>
    /// Get active delegations for the current user.
    /// </summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActiveDelegations(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var delegations = await _delegationService.GetActiveDelegationsForUserAsync(userId.Value, ct);
        return Ok(delegations.Select(MapDelegationToResponse));
    }

    /// <summary>
    /// Approve a pending delegation.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> ApproveDelegation(Guid id, CancellationToken ct)
    {
        var failure = TryGetActor(out var actor);
        if (failure != null)
            return failure;

        try
        {
            var delegation = await _delegationService.ApproveDelegationAsync(actor!, id, ct);
            return Ok(MapDelegationToResponse(delegation));
        }
        catch (DelegationAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Revoke a delegation.
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> RevokeDelegation(Guid id, CancellationToken ct)
    {
        var failure = TryGetActor(out var actor);
        if (failure != null)
            return failure;

        try
        {
            var delegation = await _delegationService.RevokeDelegationAsync(actor!, id, ct);
            return Ok(MapDelegationToResponse(delegation));
        }
        catch (DelegationAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get effective permissions for a user (including delegated). Your own, or anyone's for an admin.
    /// </summary>
    [HttpGet("effective-permissions")]
    public async Task<IActionResult> GetEffectivePermissions(
        [FromQuery] Guid userId, [FromQuery] Guid tenantId, CancellationToken ct)
    {
        var failure = TryGetActor(out var actor);
        if (failure != null)
            return failure;

        try
        {
            var permissions = await _delegationService.GetEffectivePermissionsAsync(actor!, userId, tenantId, ct);
            return Ok(new { userId, tenantId, permissions });
        }
        catch (DelegationAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
    }

    // ─── SoD Constraints ─────────────────────────────────────

    /// <summary>
    /// Create a new SoD constraint. Admin-only: the rules also govern tenant admins, so they cannot edit them.
    /// </summary>
    [HttpPost("/api/sod/constraints")]
    [Authorize(Roles = SodAdminRoles)]
    public async Task<IActionResult> CreateConstraint([FromBody] CreateSodConstraintDto dto, CancellationToken ct)
    {
        try
        {
            var severity = SodSeverity.Warning;
            if (!string.IsNullOrEmpty(dto.Severity) && Enum.TryParse<SodSeverity>(dto.Severity, true, out var parsed))
                severity = parsed;

            var constraint = await _delegationService.CreateConstraintAsync(
                dto.Name,
                dto.TenantId,
                dto.ConflictingRoleA,
                dto.ConflictingRoleB,
                dto.Description,
                severity,
                ct);

            return Ok(MapConstraintToResponse(constraint));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get SoD constraints for a tenant.
    /// </summary>
    [HttpGet("/api/sod/constraints")]
    public async Task<IActionResult> GetConstraints([FromQuery] Guid tenantId, CancellationToken ct)
    {
        var constraints = await _delegationService.GetConstraintsAsync(tenantId, ct);
        return Ok(constraints.Select(MapConstraintToResponse));
    }

    /// <summary>
    /// Get a specific SoD constraint by ID.
    /// </summary>
    [HttpGet("/api/sod/constraints/{id:guid}")]
    public async Task<IActionResult> GetConstraint(Guid id, CancellationToken ct)
    {
        var constraint = await _delegationService.GetConstraintAsync(id, ct);
        if (constraint == null)
            return NotFound(new { error = "SoD constraint not found" });

        return Ok(MapConstraintToResponse(constraint));
    }

    /// <summary>
    /// Update a SoD constraint. Admin-only.
    /// </summary>
    [HttpPut("/api/sod/constraints/{id:guid}")]
    [Authorize(Roles = SodAdminRoles)]
    public async Task<IActionResult> UpdateConstraint(Guid id, [FromBody] UpdateSodConstraintDto dto, CancellationToken ct)
    {
        try
        {
            var severity = SodSeverity.Warning;
            if (!string.IsNullOrEmpty(dto.Severity) && Enum.TryParse<SodSeverity>(dto.Severity, true, out var parsed))
                severity = parsed;

            var updated = new SodConstraint
            {
                Name = dto.Name,
                Description = dto.Description,
                Severity = severity,
                IsActive = dto.IsActive
            };

            var constraint = await _delegationService.UpdateConstraintAsync(id, updated, ct);
            return Ok(MapConstraintToResponse(constraint));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a SoD constraint. Admin-only.
    /// </summary>
    [HttpDelete("/api/sod/constraints/{id:guid}")]
    [Authorize(Roles = SodAdminRoles)]
    public async Task<IActionResult> DeleteConstraint(Guid id, CancellationToken ct)
    {
        var deleted = await _delegationService.DeleteConstraintAsync(id, ct);
        if (!deleted)
            return NotFound(new { error = "SoD constraint not found" });

        return Ok(new { message = "SoD constraint deleted" });
    }

    /// <summary>
    /// Check SoD violations for a specific user.
    /// </summary>
    [HttpPost("/api/sod/check/{userId:guid}")]
    public async Task<IActionResult> CheckSodViolations(Guid userId, CancellationToken ct)
    {
        var violations = await _delegationService.CheckSodViolationsAsync(userId, ct);
        return Ok(new
        {
            userId,
            violationsFound = violations.Count,
            violations = violations.Select(MapViolationToResponse)
        });
    }

    /// <summary>
    /// Get SoD violations, optionally filtered by tenant.
    /// </summary>
    [HttpGet("/api/sod/violations")]
    public async Task<IActionResult> GetViolations([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var violations = await _delegationService.GetViolationsAsync(tenantId, ct);
        return Ok(violations.Select(MapViolationToResponse));
    }

    /// <summary>
    /// Resolve a SoD violation.
    /// </summary>
    [HttpPost("/api/sod/violations/{id:guid}/resolve")]
    public async Task<IActionResult> ResolveViolation(Guid id, [FromBody] ResolveSodViolationDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        try
        {
            var violation = await _delegationService.ResolveViolationAsync(id, userId.Value, dto.Resolution, ct);
            return Ok(MapViolationToResponse(violation));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ─── Helpers ─────────────────────────────────────────────

    /// <summary>
    /// Builds the caller for the service's authority checks. Returns an error result (and no actor) when the
    /// token has no user id, an unreadable tenant_id, or is a service-account token (service accounts do not
    /// delegate). Global admin = SuperAdmin or SystemAdmin; same shape as IdentityProvidersController.
    /// </summary>
    private IActionResult? TryGetActor(out DelegationActor? actor)
    {
        actor = null;

        if (ServiceAccountAuthorization.IsServiceAccount(User))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Delegations are not available to service accounts" });

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        Guid? tenantId = null;
        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var parsedTenantId))
                return Unauthorized(new { error = "Invalid tenant_id in token" });
            tenantId = parsedTenantId;
        }

        actor = new DelegationActor(userId.Value, User.IsInRole("SuperAdmin") || User.IsInRole("SystemAdmin"), tenantId);
        return null;
    }

    private ObjectResult Forbidden(DelegationAccessDeniedException ex) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });

    private Guid? GetUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(claim, out var userId))
            return userId;

        return null;
    }

    private static object MapDelegationToResponse(Delegation d) => new
    {
        id = d.Id,
        delegatorUserId = d.DelegatorUserId,
        delegatorUserName = d.DelegatorUser != null ? $"{d.DelegatorUser.FirstName} {d.DelegatorUser.LastName}" : null,
        delegateUserId = d.DelegateUserId,
        delegateUserName = d.DelegateUser != null ? $"{d.DelegateUser.FirstName} {d.DelegateUser.LastName}" : null,
        tenantId = d.TenantId,
        tenantName = d.Tenant?.Name,
        permissions = d.Permissions,
        validFrom = d.ValidFrom,
        validUntil = d.ValidUntil,
        reason = d.Reason,
        isActive = d.IsActive,
        requiresApproval = d.RequiresApproval,
        status = d.Status.ToString(),
        approvedByUserId = d.ApprovedByUserId,
        approvedAt = d.ApprovedAt,
        revokedAt = d.RevokedAt,
        revokedByUserId = d.RevokedByUserId,
        createdAt = d.CreatedAt,
        updatedAt = d.UpdatedAt
    };

    private static object MapConstraintToResponse(SodConstraint c) => new
    {
        id = c.Id,
        name = c.Name,
        tenantId = c.TenantId,
        conflictingRoleA = c.ConflictingRoleA,
        roleAName = c.RoleA?.Name,
        conflictingRoleB = c.ConflictingRoleB,
        roleBName = c.RoleB?.Name,
        description = c.Description,
        severity = c.Severity.ToString(),
        isActive = c.IsActive,
        createdAt = c.CreatedAt,
        updatedAt = c.UpdatedAt
    };

    private static object MapViolationToResponse(SodViolation v) => new
    {
        id = v.Id,
        constraintId = v.ConstraintId,
        constraintName = v.Constraint?.Name,
        userId = v.UserId,
        userName = v.User != null ? $"{v.User.FirstName} {v.User.LastName}" : null,
        roleA = v.RoleA,
        roleB = v.RoleB,
        detectedAt = v.DetectedAt,
        resolution = v.Resolution,
        resolvedAt = v.ResolvedAt,
        resolvedByUserId = v.ResolvedByUserId
    };
}

// ─── DTOs ─────────────────────────────────────────────────

public record CreateDelegationDto(
    Guid? DelegatorUserId,
    Guid DelegateUserId,
    Guid TenantId,
    List<string> Permissions,
    DateTime ValidFrom,
    DateTime ValidUntil,
    string Reason,
    bool RequiresApproval = false
);

public record CreateSodConstraintDto(
    string Name,
    Guid TenantId,
    Guid ConflictingRoleA,
    Guid ConflictingRoleB,
    string Description,
    string? Severity = null
);

public record UpdateSodConstraintDto(
    string Name,
    string Description,
    string? Severity,
    bool IsActive = true
);

public record ResolveSodViolationDto(string Resolution);
