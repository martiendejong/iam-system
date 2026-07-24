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

    public ResourcePermissionController(IResourcePermissionService permissionService)
    {
        _permissionService = permissionService;
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
    /// Grant permission to user or role
    /// </summary>
    [HttpPost("grant")]
    [ProducesResponseType(typeof(ResourcePermission), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<ResourcePermission>> GrantPermission([FromBody] GrantPermissionRequest request)
    {
        var tenantId = GetTenantId();
        var grantedByUserId = GetUserId();

        if (!request.UserId.HasValue && !request.RoleId.HasValue)
            return BadRequest("Either UserId or RoleId must be provided");

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
    /// Revoke permission
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RevokePermission(Guid id)
    {
        var tenantId = GetTenantId();
        var result = await _permissionService.RevokePermissionAsync(id, tenantId);

        if (!result)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Revoke all user permissions on a resource
    /// </summary>
    [HttpDelete("user/{userId}/resource/{resourceType}/{resourceId}")]
    [ProducesResponseType(typeof(int), 200)]
    public async Task<ActionResult<int>> RevokeAllUserPermissions(
        Guid userId,
        ResourceType resourceType,
        Guid resourceId)
    {
        var tenantId = GetTenantId();
        var count = await _permissionService.RevokeAllUserPermissionsAsync(userId, resourceType, resourceId, tenantId);
        return Ok(count);
    }

    /// <summary>
    /// Revoke all role permissions on a resource
    /// </summary>
    [HttpDelete("role/{roleId}/resource/{resourceType}/{resourceId}")]
    [ProducesResponseType(typeof(int), 200)]
    public async Task<ActionResult<int>> RevokeAllRolePermissions(
        Guid roleId,
        ResourceType resourceType,
        Guid resourceId)
    {
        var tenantId = GetTenantId();
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
