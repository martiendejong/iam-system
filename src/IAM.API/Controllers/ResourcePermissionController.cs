using IAM.Core.Entities;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ResourcePermissionController : ControllerBase
{
    private readonly IResourcePermissionService _permissionService;
    private readonly ILogger<ResourcePermissionController> _logger;

    public ResourcePermissionController(IResourcePermissionService permissionService, ILogger<ResourcePermissionController> logger)
    {
        _permissionService = permissionService;
        _logger = logger;
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

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            throw new UnauthorizedAccessException("User ID not found in token");
        }
        return userId;
    }

    /// <summary>
    /// Shared gate for the four write endpoints (task 4717): the caller must hold ManageAccess on the
    /// resource or be SuperAdmin / a tenant admin, and the resource must belong to the caller's tenant.
    /// Returns null when allowed, otherwise the 403/404 response to return (nothing has been changed yet).
    /// </summary>
    private async Task<ActionResult?> DenyUnlessMayManageAccessAsync(
        Guid userId,
        Guid tenantId,
        ResourceType resourceType,
        Guid resourceId,
        PermissionAction? grantedActions,
        bool requireResourceInTenant,
        CancellationToken ct)
    {
        var decision = await _permissionService.AuthorizeManageAccessAsync(
            userId, User.IsInRole("SuperAdmin"), resourceType, resourceId, tenantId,
            grantedActions, requireResourceInTenant, ct);

        if (decision == ManageAccessDecision.Allowed)
            return null;

        _logger.LogWarning(
            "Resource permission change refused ({Decision}): user {UserId}, tenant {TenantId}, {ResourceType} {ResourceId}",
            decision, userId, tenantId, resourceType, resourceId);

        return decision switch
        {
            ManageAccessDecision.ResourceNotFound => NotFound(),
            ManageAccessDecision.ExceedsOwnPermissions => StatusCode(StatusCodes.Status403Forbidden,
                new { error = "You cannot grant actions you do not hold yourself on this resource." }),
            _ => StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Managing access to this resource requires the ManageAccess permission on it." })
        };
    }

    /// <summary>
    /// Get all permissions for tenant
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ResourcePermission>), 200)]
    public async Task<ActionResult<IEnumerable<ResourcePermission>>> GetAll()
    {
        var tenantId = GetTenantId();
        var permissions = await _permissionService.GetAllAsync(tenantId);
        return Ok(permissions);
    }

    /// <summary>
    /// Get permission by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ResourcePermission), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ResourcePermission>> GetById(Guid id)
    {
        var tenantId = GetTenantId();
        var permission = await _permissionService.GetByIdAsync(id, tenantId);

        if (permission == null)
            return NotFound();

        return Ok(permission);
    }

    /// <summary>
    /// Get permissions for a specific resource
    /// </summary>
    [HttpGet("resource/{resourceType}/{resourceId}")]
    [ProducesResponseType(typeof(IEnumerable<ResourcePermission>), 200)]
    public async Task<ActionResult<IEnumerable<ResourcePermission>>> GetByResource(
        ResourceType resourceType,
        Guid resourceId)
    {
        var tenantId = GetTenantId();
        var permissions = await _permissionService.GetByResourceAsync(resourceType, resourceId, tenantId);
        return Ok(permissions);
    }

    /// <summary>
    /// Get all permissions for a user
    /// </summary>
    [HttpGet("user/{userId}")]
    [ProducesResponseType(typeof(IEnumerable<ResourcePermission>), 200)]
    public async Task<ActionResult<IEnumerable<ResourcePermission>>> GetByUser(Guid userId)
    {
        var tenantId = GetTenantId();
        var permissions = await _permissionService.GetByUserAsync(userId, tenantId);
        return Ok(permissions);
    }

    /// <summary>
    /// Get all permissions for a role
    /// </summary>
    [HttpGet("role/{roleId}")]
    [ProducesResponseType(typeof(IEnumerable<ResourcePermission>), 200)]
    public async Task<ActionResult<IEnumerable<ResourcePermission>>> GetByRole(Guid roleId)
    {
        var tenantId = GetTenantId();
        var permissions = await _permissionService.GetByRoleAsync(roleId, tenantId);
        return Ok(permissions);
    }

    /// <summary>
    /// Check if user has specific permission on resource
    /// </summary>
    [HttpGet("check")]
    [ProducesResponseType(typeof(bool), 200)]
    public async Task<ActionResult<bool>> CheckPermission(
        [FromQuery] ResourceType resourceType,
        [FromQuery] Guid resourceId,
        [FromQuery] PermissionAction action)
    {
        var tenantId = GetTenantId();
        var userId = GetUserId();

        var hasPermission = await _permissionService.HasPermissionAsync(
            userId, resourceType, resourceId, action, tenantId);

        return Ok(hasPermission);
    }

    /// <summary>
    /// Get effective permissions for user on resource (includes inherited)
    /// </summary>
    [HttpGet("effective")]
    [ProducesResponseType(typeof(PermissionAction), 200)]
    public async Task<ActionResult<PermissionAction>> GetEffectivePermissions(
        [FromQuery] ResourceType resourceType,
        [FromQuery] Guid resourceId)
    {
        var tenantId = GetTenantId();
        var userId = GetUserId();

        var permissions = await _permissionService.GetEffectivePermissionsAsync(
            userId, resourceType, resourceId, tenantId);

        return Ok(permissions);
    }

    /// <summary>
    /// Get inherited permissions for a resource
    /// </summary>
    [HttpGet("inherited/{resourceType}/{resourceId}")]
    [ProducesResponseType(typeof(IEnumerable<ResourcePermission>), 200)]
    public async Task<ActionResult<IEnumerable<ResourcePermission>>> GetInheritedPermissions(
        ResourceType resourceType,
        Guid resourceId)
    {
        var tenantId = GetTenantId();
        var permissions = await _permissionService.GetInheritedPermissionsAsync(resourceType, resourceId, tenantId);
        return Ok(permissions);
    }

    /// <summary>
    /// Get all accessible resources of a type for current user
    /// </summary>
    [HttpGet("accessible/{resourceType}")]
    [ProducesResponseType(typeof(IEnumerable<Guid>), 200)]
    public async Task<ActionResult<IEnumerable<Guid>>> GetAccessibleResources(ResourceType resourceType)
    {
        var tenantId = GetTenantId();
        var userId = GetUserId();

        var resourceIds = await _permissionService.GetAccessibleResourcesAsync(userId, resourceType, tenantId);
        return Ok(resourceIds);
    }

    /// <summary>
    /// Grant permission to user or role. Requires ManageAccess on the resource (or SuperAdmin / tenant
    /// admin) and the resource must belong to the caller's tenant; non-admins cannot grant actions they
    /// do not hold themselves.
    /// </summary>
    [HttpPost("grant")]
    [ProducesResponseType(typeof(ResourcePermission), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ResourcePermission>> GrantPermission([FromBody] GrantPermissionRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var grantedByUserId = GetUserId();

        if (!request.UserId.HasValue && !request.RoleId.HasValue)
            return BadRequest("Either UserId or RoleId must be provided");

        var denied = await DenyUnlessMayManageAccessAsync(
            grantedByUserId, tenantId, request.ResourceType, request.ResourceId, request.Actions,
            requireResourceInTenant: true, ct);
        if (denied != null)
            return denied;

        var permission = await _permissionService.GrantPermissionAsync(
            request.UserId,
            request.RoleId,
            request.ResourceType,
            request.ResourceId,
            request.Actions,
            tenantId,
            grantedByUserId,
            request.InheritToChildren,
            request.ValidFrom,
            request.ValidUntil,
            request.Reason);

        return CreatedAtAction(nameof(GetById), new { id = permission.Id }, permission);
    }

    /// <summary>
    /// Revoke permission. Requires ManageAccess on the permission's resource (or SuperAdmin / tenant admin).
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RevokePermission(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var userId = GetUserId();

        // The resource comes from the stored row (looked up by tenant), never from the request.
        var existing = await _permissionService.GetByIdAsync(id, tenantId);
        if (existing == null)
            return NotFound();

        var denied = await DenyUnlessMayManageAccessAsync(
            userId, tenantId, existing.ResourceType, existing.ResourceId, grantedActions: null,
            requireResourceInTenant: false, ct);
        if (denied != null)
            return denied;

        var result = await _permissionService.RevokePermissionAsync(id, tenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Revoke all user permissions on a resource. Requires ManageAccess on the resource (or SuperAdmin / tenant admin).
    /// </summary>
    [HttpDelete("user/{userId}/resource/{resourceType}/{resourceId}")]
    [ProducesResponseType(typeof(int), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<int>> RevokeAllUserPermissions(
        Guid userId,
        ResourceType resourceType,
        Guid resourceId,
        CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var denied = await DenyUnlessMayManageAccessAsync(
            GetUserId(), tenantId, resourceType, resourceId, grantedActions: null,
            requireResourceInTenant: true, ct);
        if (denied != null)
            return denied;

        var count = await _permissionService.RevokeAllUserPermissionsAsync(userId, resourceType, resourceId, tenantId);
        return Ok(count);
    }

    /// <summary>
    /// Revoke all role permissions on a resource. Requires ManageAccess on the resource (or SuperAdmin / tenant admin).
    /// </summary>
    [HttpDelete("role/{roleId}/resource/{resourceType}/{resourceId}")]
    [ProducesResponseType(typeof(int), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<int>> RevokeAllRolePermissions(
        Guid roleId,
        ResourceType resourceType,
        Guid resourceId,
        CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var denied = await DenyUnlessMayManageAccessAsync(
            GetUserId(), tenantId, resourceType, resourceId, grantedActions: null,
            requireResourceInTenant: true, ct);
        if (denied != null)
            return denied;

        var count = await _permissionService.RevokeAllRolePermissionsAsync(roleId, resourceType, resourceId, tenantId);
        return Ok(count);
    }
}

public class GrantPermissionRequest
{
    public Guid? UserId { get; set; }
    public Guid? RoleId { get; set; }
    public ResourceType ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public PermissionAction Actions { get; set; }
    public bool InheritToChildren { get; set; } = true;
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string? Reason { get; set; }
}
