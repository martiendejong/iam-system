using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class GroupService : IGroupService
{
    private readonly IAMDbContext _context;

    public GroupService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<Group> CreateGroupAsync(Group group, CancellationToken ct = default)
    {
        // Verify tenant exists
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == group.TenantId, ct);
        if (!tenantExists)
        {
            throw new InvalidOperationException("Tenant not found");
        }

        // Verify parent group exists if specified
        if (group.ParentGroupId.HasValue)
        {
            var parentExists = await _context.Groups.AnyAsync(g => g.Id == group.ParentGroupId.Value, ct);
            if (!parentExists)
            {
                throw new InvalidOperationException("Parent group not found");
            }
        }

        _context.Groups.Add(group);
        await _context.SaveChangesAsync(ct);

        return group;
    }

    public async Task<Group?> GetGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        return await _context.Groups
            .Include(g => g.Tenant)
            .Include(g => g.ParentGroup)
            .Include(g => g.ChildGroups)
            .Include(g => g.Members.Where(m => m.IsActive))
                .ThenInclude(m => m.User)
            .Include(g => g.GroupRoles)
                .ThenInclude(gr => gr.Role)
            .FirstOrDefaultAsync(g => g.Id == groupId, ct);
    }

    public async Task<List<Group>> GetGroupsByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.Groups
            .Include(g => g.ParentGroup)
            .Include(g => g.Members.Where(m => m.IsActive))
            .Where(g => g.TenantId == tenantId && g.IsActive)
            .OrderBy(g => g.GroupType)
            .ThenBy(g => g.Name)
            .ToListAsync(ct);
    }

    public async Task<Group> UpdateGroupAsync(Guid groupId, string name, string? description, string? metadata, CancellationToken ct = default)
    {
        var group = await _context.Groups.FindAsync(new object[] { groupId }, ct);
        if (group == null)
        {
            throw new InvalidOperationException("Group not found");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            group.Name = name;
        }

        group.Description = description;
        group.Metadata = metadata;
        group.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        return group;
    }

    public async Task<bool> DeleteGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        var group = await _context.Groups
            .Include(g => g.ChildGroups)
            .Include(g => g.Members)
            .Include(g => g.GroupRoles)
            .FirstOrDefaultAsync(g => g.Id == groupId, ct);

        if (group == null)
        {
            return false;
        }

        // Prevent deletion if group has child groups
        if (group.ChildGroups.Any())
        {
            throw new InvalidOperationException("Cannot delete group with child groups. Remove child groups first.");
        }

        // Remove all memberships and role assignments
        _context.GroupMemberships.RemoveRange(group.Members);
        _context.GroupRoles.RemoveRange(group.GroupRoles);
        _context.Groups.Remove(group);

        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<GroupMembership> AddMemberAsync(Guid groupId, Guid userId, string role = "member", DateTime? expiresAt = null, CancellationToken ct = default)
    {
        // Verify group exists
        var groupExists = await _context.Groups.AnyAsync(g => g.Id == groupId, ct);
        if (!groupExists)
        {
            throw new InvalidOperationException("Group not found");
        }

        // Verify user exists
        var userExists = await _context.Users.AnyAsync(u => u.Id == userId, ct);
        if (!userExists)
        {
            throw new InvalidOperationException("User not found");
        }

        // Check if already a member
        var existingMembership = await _context.GroupMemberships
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct);

        if (existingMembership != null)
        {
            // Reactivate if previously removed
            existingMembership.IsActive = true;
            existingMembership.Role = role;
            existingMembership.ExpiresAt = expiresAt;
            existingMembership.JoinedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);
            return existingMembership;
        }

        var membership = new GroupMembership
        {
            GroupId = groupId,
            UserId = userId,
            Role = role,
            ExpiresAt = expiresAt
        };

        _context.GroupMemberships.Add(membership);
        await _context.SaveChangesAsync(ct);

        return membership;
    }

    public async Task<bool> RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var membership = await _context.GroupMemberships
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId && m.IsActive, ct);

        if (membership == null)
        {
            return false;
        }

        membership.IsActive = false;
        await _context.SaveChangesAsync(ct);

        return true;
    }

    public async Task<List<GroupMembership>> GetMembersAsync(Guid groupId, CancellationToken ct = default)
    {
        return await _context.GroupMemberships
            .Include(m => m.User)
            .Where(m => m.GroupId == groupId && m.IsActive)
            .OrderBy(m => m.Role)
            .ThenBy(m => m.JoinedAt)
            .ToListAsync(ct);
    }

    public async Task<List<Group>> GetUserGroupsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.GroupMemberships
            .Include(m => m.Group)
                .ThenInclude(g => g!.Tenant)
            .Where(m => m.UserId == userId
                && m.IsActive
                && (m.ExpiresAt == null || m.ExpiresAt > DateTime.UtcNow))
            .Select(m => m.Group!)
            .Where(g => g.IsActive)
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
    }

    public async Task<GroupRole> AssignRoleToGroupAsync(Guid groupId, Guid roleId, Guid tenantId, Guid? assignedByUserId = null, CancellationToken ct = default)
    {
        // Verify group exists
        var groupExists = await _context.Groups.AnyAsync(g => g.Id == groupId, ct);
        if (!groupExists)
        {
            throw new InvalidOperationException("Group not found");
        }

        // Verify role exists
        var roleExists = await _context.Roles.AnyAsync(r => r.Id == roleId, ct);
        if (!roleExists)
        {
            throw new InvalidOperationException("Role not found");
        }

        // Verify tenant exists
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == tenantId, ct);
        if (!tenantExists)
        {
            throw new InvalidOperationException("Tenant not found");
        }

        // Check if role is already assigned to this group
        var existingAssignment = await _context.GroupRoles
            .FirstOrDefaultAsync(gr => gr.GroupId == groupId && gr.RoleId == roleId, ct);

        if (existingAssignment != null)
        {
            throw new InvalidOperationException("Role is already assigned to this group");
        }

        var groupRole = new GroupRole
        {
            GroupId = groupId,
            RoleId = roleId,
            TenantId = tenantId,
            AssignedByUserId = assignedByUserId
        };

        _context.GroupRoles.Add(groupRole);
        await _context.SaveChangesAsync(ct);

        return groupRole;
    }

    public async Task<bool> RemoveRoleFromGroupAsync(Guid groupId, Guid roleId, CancellationToken ct = default)
    {
        var groupRole = await _context.GroupRoles
            .FirstOrDefaultAsync(gr => gr.GroupId == groupId && gr.RoleId == roleId, ct);

        if (groupRole == null)
        {
            return false;
        }

        _context.GroupRoles.Remove(groupRole);
        await _context.SaveChangesAsync(ct);

        return true;
    }

    public async Task<List<string>> GetEffectivePermissionsAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        // Step 1: Get all active, non-expired groups the user belongs to
        var userGroupIds = await _context.GroupMemberships
            .Where(m => m.UserId == userId
                && m.IsActive
                && (m.ExpiresAt == null || m.ExpiresAt > DateTime.UtcNow))
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        if (!userGroupIds.Any())
        {
            return new List<string>();
        }

        // Step 2: Get all roles assigned to those groups for the given tenant
        var roleIds = await _context.GroupRoles
            .Where(gr => userGroupIds.Contains(gr.GroupId) && gr.TenantId == tenantId)
            .Select(gr => gr.RoleId)
            .Distinct()
            .ToListAsync(ct);

        if (!roleIds.Any())
        {
            return new List<string>();
        }

        // Step 3: Collect all permissions from those roles
        var roles = await _context.Roles
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Permissions)
            .ToListAsync(ct);

        // Step 4: Parse JSON permission arrays and deduplicate
        var allPermissions = new HashSet<string>();
        foreach (var permissionsJson in roles)
        {
            if (string.IsNullOrWhiteSpace(permissionsJson))
            {
                continue;
            }

            try
            {
                var permissions = JsonSerializer.Deserialize<List<string>>(permissionsJson);
                if (permissions != null)
                {
                    foreach (var permission in permissions)
                    {
                        allPermissions.Add(permission);
                    }
                }
            }
            catch (JsonException)
            {
                // Skip malformed permission JSON
            }
        }

        return allPermissions.OrderBy(p => p).ToList();
    }
}
