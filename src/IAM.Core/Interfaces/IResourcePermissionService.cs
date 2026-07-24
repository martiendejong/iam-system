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
}
