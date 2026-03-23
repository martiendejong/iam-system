using IAM.Core.Entities;
using IAM.Core.Interfaces;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class ResourcePermissionService : IResourcePermissionService
{
    private readonly IAMDbContext _context;

    public ResourcePermissionService(IAMDbContext context)
    {
        _context = context;
    }

    #region CRUD Operations

    public async Task<ResourcePermission?> GetByIdAsync(Guid id, Guid tenantId)
    {
        return await _context.ResourcePermissions
            .Include(p => p.User)
            .Include(p => p.Role)
            .Include(p => p.GrantedByUser)
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId);
    }

    public async Task<IEnumerable<ResourcePermission>> GetAllAsync(Guid tenantId)
    {
        return await _context.ResourcePermissions
            .Where(p => p.TenantId == tenantId && p.IsActive)
            .Include(p => p.User)
            .Include(p => p.Role)
            .OrderByDescending(p => p.GrantedAt)
            .ToListAsync();
    }

    public async Task<ResourcePermission> CreateAsync(ResourcePermission permission)
    {
        permission.GrantedAt = DateTime.UtcNow;
        permission.IsActive = true;

        _context.ResourcePermissions.Add(permission);
        await _context.SaveChangesAsync();

        return permission;
    }

    public async Task<ResourcePermission> UpdateAsync(ResourcePermission permission)
    {
        permission.UpdatedAt = DateTime.UtcNow;

        _context.ResourcePermissions.Update(permission);
        await _context.SaveChangesAsync();

        return permission;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        var permission = await _context.ResourcePermissions
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId);

        if (permission == null)
            return false;

        // Soft delete
        permission.IsActive = false;
        permission.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    #endregion

    #region Query Permissions

    public async Task<IEnumerable<ResourcePermission>> GetByResourceAsync(ResourceType resourceType, Guid resourceId, Guid tenantId)
    {
        return await _context.ResourcePermissions
            .Where(p => p.ResourceType == resourceType &&
                       p.ResourceId == resourceId &&
                       p.TenantId == tenantId &&
                       p.IsActive)
            .Include(p => p.User)
            .Include(p => p.Role)
            .ToListAsync();
    }

    public async Task<IEnumerable<ResourcePermission>> GetByUserAsync(Guid userId, Guid tenantId)
    {
        return await _context.ResourcePermissions
            .Where(p => p.UserId == userId && p.TenantId == tenantId && p.IsActive)
            .Include(p => p.Role)
            .OrderByDescending(p => p.GrantedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<ResourcePermission>> GetByRoleAsync(Guid roleId, Guid tenantId)
    {
        return await _context.ResourcePermissions
            .Where(p => p.RoleId == roleId && p.TenantId == tenantId && p.IsActive)
            .OrderByDescending(p => p.GrantedAt)
            .ToListAsync();
    }

    #endregion

    #region Permission Checking (with Hierarchical Inheritance)

    public async Task<bool> HasPermissionAsync(Guid userId, ResourceType resourceType, Guid resourceId, PermissionAction action, Guid tenantId)
    {
        var effectivePermissions = await GetEffectivePermissionsAsync(userId, resourceType, resourceId, tenantId);
        return effectivePermissions.HasFlag(action);
    }

    public async Task<PermissionAction> GetEffectivePermissionsAsync(Guid userId, ResourceType resourceType, Guid resourceId, Guid tenantId)
    {
        var allPermissions = PermissionAction.None;

        // Get user's roles (only active roles that haven't expired)
        var now = DateTime.UtcNow;
        var userRoleIds = await _context.UserRoles
            .Where(ur => ur.UserId == userId &&
                        ur.TenantId == tenantId &&
                        (!ur.ExpiresAt.HasValue || ur.ExpiresAt.Value > now))
            .Select(ur => ur.RoleId)
            .ToListAsync();

        // 1. Check direct permissions on this resource
        var directPermissions = await _context.ResourcePermissions
            .Where(p => p.ResourceType == resourceType &&
                       p.ResourceId == resourceId &&
                       p.TenantId == tenantId &&
                       p.IsActive &&
                       (p.UserId == userId || (p.RoleId.HasValue && userRoleIds.Contains(p.RoleId.Value))) &&
                       (!p.ValidFrom.HasValue || p.ValidFrom.Value <= now) &&
                       (!p.ValidUntil.HasValue || p.ValidUntil.Value >= now))
            .ToListAsync();

        foreach (var permission in directPermissions)
        {
            allPermissions |= permission.Actions;
        }

        // 2. Check inherited permissions from parent resources
        var inheritedPermissions = await GetInheritedPermissionsForUserAsync(userId, userRoleIds, resourceType, resourceId, tenantId, now);

        foreach (var permission in inheritedPermissions)
        {
            allPermissions |= permission.Actions;
        }

        return allPermissions;
    }

    /// <summary>
    /// Get inherited permissions by traversing up the hierarchy
    /// Location -> Building -> Floor -> Room/RoomGroup -> IoTDevice
    /// </summary>
    private async Task<List<ResourcePermission>> GetInheritedPermissionsForUserAsync(
        Guid userId,
        List<Guid> userRoleIds,
        ResourceType resourceType,
        Guid resourceId,
        Guid tenantId,
        DateTime now)
    {
        var inheritedPermissions = new List<ResourcePermission>();

        // Get parent resource IDs by traversing up the hierarchy
        var parentResources = await GetParentResourcesAsync(resourceType, resourceId, tenantId);

        foreach (var (parentType, parentId) in parentResources)
        {
            // Find permissions on parent resource that have InheritToChildren = true
            var parentPermissions = await _context.ResourcePermissions
                .Where(p => p.ResourceType == parentType &&
                           p.ResourceId == parentId &&
                           p.TenantId == tenantId &&
                           p.IsActive &&
                           p.InheritToChildren &&
                           (p.UserId == userId || (p.RoleId.HasValue && userRoleIds.Contains(p.RoleId.Value))) &&
                           (!p.ValidFrom.HasValue || p.ValidFrom.Value <= now) &&
                           (!p.ValidUntil.HasValue || p.ValidUntil.Value >= now))
                .ToListAsync();

            inheritedPermissions.AddRange(parentPermissions);
        }

        return inheritedPermissions;
    }

    /// <summary>
    /// Get all parent resources in the hierarchy for a given resource
    /// Returns list of (ResourceType, ResourceId) tuples from immediate parent to top-level
    /// </summary>
    private async Task<List<(ResourceType Type, Guid Id)>> GetParentResourcesAsync(ResourceType resourceType, Guid resourceId, Guid tenantId)
    {
        var parents = new List<(ResourceType, Guid)>();

        switch (resourceType)
        {
            case ResourceType.IoTDevice:
                var device = await _context.IoTDevices
                    .Include(d => d.Room)
                        .ThenInclude(r => r.Floor)
                            .ThenInclude(f => f.Building)
                                .ThenInclude(b => b.Location)
                    .FirstOrDefaultAsync(d => d.Id == resourceId && d.TenantId == tenantId);

                if (device != null)
                {
                    parents.Add((ResourceType.Room, device.RoomId));
                    if (device.Room != null)
                    {
                        parents.Add((ResourceType.Floor, device.Room.FloorId));
                        if (device.Room.Floor != null)
                        {
                            parents.Add((ResourceType.Building, device.Room.Floor.BuildingId));
                            if (device.Room.Floor.Building != null)
                            {
                                parents.Add((ResourceType.Location, device.Room.Floor.Building.LocationId));
                            }
                        }
                    }
                }
                break;

            case ResourceType.Room:
                var room = await _context.Rooms
                    .Include(r => r.Floor)
                        .ThenInclude(f => f.Building)
                            .ThenInclude(b => b.Location)
                    .FirstOrDefaultAsync(r => r.Id == resourceId && r.TenantId == tenantId);

                if (room != null)
                {
                    parents.Add((ResourceType.Floor, room.FloorId));
                    if (room.Floor != null)
                    {
                        parents.Add((ResourceType.Building, room.Floor.BuildingId));
                        if (room.Floor.Building != null)
                        {
                            parents.Add((ResourceType.Location, room.Floor.Building.LocationId));
                        }
                    }
                }
                break;

            case ResourceType.Floor:
                var floor = await _context.Floors
                    .Include(f => f.Building)
                        .ThenInclude(b => b.Location)
                    .FirstOrDefaultAsync(f => f.Id == resourceId && f.TenantId == tenantId);

                if (floor != null)
                {
                    parents.Add((ResourceType.Building, floor.BuildingId));
                    if (floor.Building != null)
                    {
                        parents.Add((ResourceType.Location, floor.Building.LocationId));
                    }
                }
                break;

            case ResourceType.Building:
                var building = await _context.Buildings
                    .Include(b => b.Location)
                    .FirstOrDefaultAsync(b => b.Id == resourceId && b.TenantId == tenantId);

                if (building != null)
                {
                    parents.Add((ResourceType.Location, building.LocationId));
                }
                break;

            case ResourceType.RoomGroup:
                var roomGroup = await _context.RoomGroups
                    .Include(rg => rg.Floor)
                        .ThenInclude(f => f!.Building)
                            .ThenInclude(b => b.Location)
                    .Include(rg => rg.Building)
                        .ThenInclude(b => b!.Location)
                    .FirstOrDefaultAsync(rg => rg.Id == resourceId && rg.TenantId == tenantId);

                if (roomGroup != null)
                {
                    // RoomGroup can belong to Floor or Building directly
                    if (roomGroup.FloorId.HasValue && roomGroup.Floor != null)
                    {
                        parents.Add((ResourceType.Floor, roomGroup.FloorId.Value));
                        if (roomGroup.Floor.Building != null)
                        {
                            parents.Add((ResourceType.Building, roomGroup.Floor.BuildingId));
                            if (roomGroup.Floor.Building.Location != null)
                            {
                                parents.Add((ResourceType.Location, roomGroup.Floor.Building.LocationId));
                            }
                        }
                    }
                    else if (roomGroup.BuildingId.HasValue && roomGroup.Building != null)
                    {
                        parents.Add((ResourceType.Building, roomGroup.BuildingId.Value));
                        if (roomGroup.Building.Location != null)
                        {
                            parents.Add((ResourceType.Location, roomGroup.Building.LocationId));
                        }
                    }
                }
                break;

            case ResourceType.Location:
                // Location is top-level, no parents
                break;
        }

        return parents;
    }

    #endregion

    #region Hierarchical Queries

    public async Task<IEnumerable<ResourcePermission>> GetInheritedPermissionsAsync(ResourceType resourceType, Guid resourceId, Guid tenantId)
    {
        var parentResources = await GetParentResourcesAsync(resourceType, resourceId, tenantId);
        var inheritedPermissions = new List<ResourcePermission>();

        foreach (var (parentType, parentId) in parentResources)
        {
            var permissions = await _context.ResourcePermissions
                .Where(p => p.ResourceType == parentType &&
                           p.ResourceId == parentId &&
                           p.TenantId == tenantId &&
                           p.IsActive &&
                           p.InheritToChildren)
                .Include(p => p.User)
                .Include(p => p.Role)
                .ToListAsync();

            inheritedPermissions.AddRange(permissions);
        }

        return inheritedPermissions;
    }

    public async Task<IEnumerable<Guid>> GetAccessibleResourcesAsync(Guid userId, ResourceType resourceType, Guid tenantId)
    {
        var now = DateTime.UtcNow;
        var userRoleIds = await _context.UserRoles
            .Where(ur => ur.UserId == userId &&
                        ur.TenantId == tenantId &&
                        (!ur.ExpiresAt.HasValue || ur.ExpiresAt.Value > now))
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var accessibleResources = new HashSet<Guid>();

        // Get all resources of the requested type
        IQueryable<Guid> resourceIds = resourceType switch
        {
            ResourceType.Location => _context.Locations.Where(l => l.TenantId == tenantId && l.IsActive).Select(l => l.Id),
            ResourceType.Building => _context.Buildings.Where(b => b.TenantId == tenantId && b.IsActive).Select(b => b.Id),
            ResourceType.Floor => _context.Floors.Where(f => f.TenantId == tenantId && f.IsActive).Select(f => f.Id),
            ResourceType.Room => _context.Rooms.Where(r => r.TenantId == tenantId && r.IsActive).Select(r => r.Id),
            ResourceType.RoomGroup => _context.RoomGroups.Where(rg => rg.TenantId == tenantId && rg.IsActive).Select(rg => rg.Id),
            ResourceType.IoTDevice => _context.IoTDevices.Where(d => d.TenantId == tenantId && d.IsActive).Select(d => d.Id),
            _ => throw new ArgumentException($"Invalid resource type: {resourceType}")
        };

        // Check each resource for access
        foreach (var resourceId in await resourceIds.ToListAsync())
        {
            var hasAccess = await HasPermissionAsync(userId, resourceType, resourceId, PermissionAction.View, tenantId);
            if (hasAccess)
            {
                accessibleResources.Add(resourceId);
            }
        }

        return accessibleResources;
    }

    #endregion

    #region Grant/Revoke Helpers

    public async Task<ResourcePermission> GrantPermissionAsync(
        Guid? userId,
        Guid? roleId,
        ResourceType resourceType,
        Guid resourceId,
        PermissionAction actions,
        Guid tenantId,
        Guid grantedByUserId,
        bool inheritToChildren = true,
        DateTime? validFrom = null,
        DateTime? validUntil = null,
        string? reason = null)
    {
        if (!userId.HasValue && !roleId.HasValue)
        {
            throw new ArgumentException("Either userId or roleId must be provided");
        }

        var permission = new ResourcePermission
        {
            UserId = userId,
            RoleId = roleId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Actions = actions,
            TenantId = tenantId,
            GrantedByUserId = grantedByUserId,
            InheritToChildren = inheritToChildren,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Reason = reason
        };

        // Set the appropriate navigation property ID based on resource type
        switch (resourceType)
        {
            case ResourceType.Location:
                permission.LocationId = resourceId;
                break;
            case ResourceType.Building:
                permission.BuildingId = resourceId;
                break;
            case ResourceType.Floor:
                permission.FloorId = resourceId;
                break;
            case ResourceType.Room:
                permission.RoomId = resourceId;
                break;
            case ResourceType.RoomGroup:
                permission.RoomGroupId = resourceId;
                break;
            case ResourceType.IoTDevice:
                permission.IoTDeviceId = resourceId;
                break;
        }

        return await CreateAsync(permission);
    }

    public async Task<bool> RevokePermissionAsync(Guid permissionId, Guid tenantId)
    {
        return await DeleteAsync(permissionId, tenantId);
    }

    public async Task<int> RevokeAllUserPermissionsAsync(Guid userId, ResourceType resourceType, Guid resourceId, Guid tenantId)
    {
        var permissions = await _context.ResourcePermissions
            .Where(p => p.UserId == userId &&
                       p.ResourceType == resourceType &&
                       p.ResourceId == resourceId &&
                       p.TenantId == tenantId &&
                       p.IsActive)
            .ToListAsync();

        foreach (var permission in permissions)
        {
            permission.IsActive = false;
            permission.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return permissions.Count;
    }

    public async Task<int> RevokeAllRolePermissionsAsync(Guid roleId, ResourceType resourceType, Guid resourceId, Guid tenantId)
    {
        var permissions = await _context.ResourcePermissions
            .Where(p => p.RoleId == roleId &&
                       p.ResourceType == resourceType &&
                       p.ResourceId == resourceId &&
                       p.TenantId == tenantId &&
                       p.IsActive)
            .ToListAsync();

        foreach (var permission in permissions)
        {
            permission.IsActive = false;
            permission.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return permissions.Count;
    }

    #endregion
}
