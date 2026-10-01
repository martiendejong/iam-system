using IAM.Core.Entities;

namespace IAM.Core.Interfaces;

/// <summary>
/// Service for managing resource permissions (hierarchical access control)
/// </summary>
public interface IResourcePermissionService
{
    // Permission CRUD
    Task<ResourcePermission?> GetByIdAsync(Guid id, Guid tenantId);
    Task<IEnumerable<ResourcePermission>> GetAllAsync(Guid tenantId);
    Task<ResourcePermission> CreateAsync(ResourcePermission permission);
    Task<ResourcePermission> UpdateAsync(ResourcePermission permission);
    Task<bool> DeleteAsync(Guid id, Guid tenantId);

    // Query permissions by resource
    Task<IEnumerable<ResourcePermission>> GetByResourceAsync(ResourceType resourceType, Guid resourceId, Guid tenantId);
    Task<IEnumerable<ResourcePermission>> GetByUserAsync(Guid userId, Guid tenantId);
    Task<IEnumerable<ResourcePermission>> GetByRoleAsync(Guid roleId, Guid tenantId);

    // Check permissions (with inheritance)
    Task<bool> HasPermissionAsync(Guid userId, ResourceType resourceType, Guid resourceId, PermissionAction action, Guid tenantId);
    Task<PermissionAction> GetEffectivePermissionsAsync(Guid userId, ResourceType resourceType, Guid resourceId, Guid tenantId);

    // Hierarchical queries
    Task<IEnumerable<ResourcePermission>> GetInheritedPermissionsAsync(ResourceType resourceType, Guid resourceId, Guid tenantId);
    Task<IEnumerable<Guid>> GetAccessibleResourcesAsync(Guid userId, ResourceType resourceType, Guid tenantId);

    // Grant/revoke helpers
    Task<ResourcePermission> GrantPermissionAsync(Guid? userId, Guid? roleId, ResourceType resourceType, Guid resourceId, PermissionAction actions, Guid tenantId, Guid grantedByUserId, bool inheritToChildren = true, DateTime? validFrom = null, DateTime? validUntil = null, string? reason = null);
    Task<bool> RevokePermissionAsync(Guid permissionId, Guid tenantId);
    Task<int> RevokeAllUserPermissionsAsync(Guid userId, ResourceType resourceType, Guid resourceId, Guid tenantId);
    Task<int> RevokeAllRolePermissionsAsync(Guid roleId, ResourceType resourceType, Guid resourceId, Guid tenantId);

    /// <summary>
    /// Decides whether <paramref name="userId"/> may grant or revoke permissions on a resource (task 4717).
    /// Allowed for a global admin (<paramref name="isSuperAdmin"/>), a tenant admin of
    /// <paramref name="tenantId"/>, or a caller holding ManageAccess on the resource. A caller who is not
    /// an admin additionally cannot hand out actions beyond what they hold themselves
    /// (<paramref name="grantedActions"/>; pass null for revokes).
    /// </summary>
    /// <param name="requireResourceInTenant">
    /// True when the resource id comes from the request (grant, revoke-all): it must exist in the tenant.
    /// False when it comes from a stored permission row that was already looked up by tenant.
    /// </param>
    Task<ManageAccessDecision> AuthorizeManageAccessAsync(
        Guid userId,
        bool isSuperAdmin,
        ResourceType resourceType,
        Guid resourceId,
        Guid tenantId,
        PermissionAction? grantedActions,
        bool requireResourceInTenant,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="IResourcePermissionService.AuthorizeManageAccessAsync"/>.
/// </summary>
public enum ManageAccessDecision
{
    Allowed,

    /// <summary>The resource does not exist in the caller's tenant (missing, or owned by another tenant).</summary>
    ResourceNotFound,

    /// <summary>The caller neither is an admin nor holds ManageAccess on the resource.</summary>
    Forbidden,

    /// <summary>The caller holds ManageAccess but tried to grant actions they do not hold themselves.</summary>
    ExceedsOwnPermissions
}
