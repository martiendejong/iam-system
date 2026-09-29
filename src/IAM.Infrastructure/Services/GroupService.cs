using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

/// <summary>
/// The only writer of GroupMemberships/GroupRoles for the groups API. Task 4056: group roles
/// (owner/admin/member) are ENFORCED here, so every caller path gets the same rules:
/// - the creator of a group becomes its owner (same save);
/// - only group owners/admins (or global admins) may mutate a group;
/// - only owners (or global admins) may grant/remove owner/admin, or delete the group;
/// - the last active owner can never be removed or demoted (not even by a global admin);
/// - attaching permission roles to a group is global-admin only.
/// A tenant_id claim on the caller's token must match the group's tenant; password-login tokens
/// (no tenant_id) derive authority purely from the caller's own active membership in the group.
/// </summary>
public class GroupService : IGroupService
{
    private readonly IAMDbContext _context;

    public GroupService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<Group> CreateGroupAsync(Group group, GroupActor actor, CancellationToken ct = default)
    {
        // Verify tenant exists
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == group.TenantId, ct);
        if (!tenantExists)
        {
            throw new InvalidOperationException("Tenant not found");
        }

        // Creating a group inside a tenant requires membership of that tenant (a UserRole scoped
        // to it) or global admin. A tenant-scoped token must match the target tenant.
        if (!actor.IsGlobalAdmin)
        {
            if (actor.TenantId.HasValue && actor.TenantId.Value != group.TenantId)
            {
                throw new GroupAccessDeniedException("Token is scoped to a different tenant");
            }

            var now = DateTime.UtcNow;
            var isTenantMember = await _context.UserRoles.AnyAsync(ur =>
                ur.UserId == actor.UserId
                && ur.TenantId == group.TenantId
                && (ur.ExpiresAt == null || ur.ExpiresAt > now), ct);
            if (!isTenantMember)
            {
                throw new GroupAccessDeniedException("Only members of the tenant (or a global admin) can create a group in it");
            }
        }

        // The creator becomes the group's owner in the same save, so it must be a real user.
        var creatorExists = await _context.Users.AnyAsync(u => u.Id == actor.UserId, ct);
        if (!creatorExists)
        {
            throw new InvalidOperationException("Creator user not found");
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
        _context.GroupMemberships.Add(new GroupMembership
        {
            GroupId = group.Id,
            UserId = actor.UserId,
            Role = GroupRoles.Owner
        });
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

    public async Task<Group> UpdateGroupAsync(Guid groupId, string name, string? description, string? metadata, GroupActor actor, CancellationToken ct = default)
    {
        var group = await _context.Groups.FindAsync(new object[] { groupId }, ct);
        if (group == null)
        {
            throw new InvalidOperationException("Group not found");
        }

        await RequireGroupAuthorityAsync(group, actor, ownerOnly: false, ct);

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

    public async Task<bool> DeleteGroupAsync(Guid groupId, GroupActor actor, CancellationToken ct = default)
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

        // Deleting a group is owner-only (or global admin).
        await RequireGroupAuthorityAsync(group, actor, ownerOnly: true, ct);

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

    public async Task<GroupMembership> AddMemberAsync(Guid groupId, Guid userId, GroupActor actor, string role = GroupRoles.Member, DateTime? expiresAt = null, CancellationToken ct = default)
    {
        var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group == null)
        {
            throw new InvalidOperationException("Group not found");
        }

        // Role values are a closed set.
        var newRole = GroupRoles.Normalize(role);
        if (newRole == null)
        {
            throw new InvalidOperationException("Invalid group role. Valid roles: owner, admin, member");
        }

        // Verify user exists
        var userExists = await _context.Users.AnyAsync(u => u.Id == userId, ct);
        if (!userExists)
        {
            throw new InvalidOperationException("User not found");
        }

        // Only owners/admins of the group (or global admins) manage membership at all. A plain
        // member (or an outsider) re-adding themselves gets 403 here: no self-promotion.
        var callerRole = await RequireGroupAuthorityAsync(group, actor, ownerOnly: false, ct);

        var existingMembership = await _context.GroupMemberships
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct);

        var now = DateTime.UtcNow;
        var currentRole = existingMembership != null
            && existingMembership.IsActive
            && (existingMembership.ExpiresAt == null || existingMembership.ExpiresAt > now)
                ? GroupRoles.Normalize(existingMembership.Role)
                : null;

        // Granting owner/admin, or changing someone who currently IS owner/admin (add-member
        // upserts the role, so this is also the demotion path), is owner-only.
        var touchesPrivilegedRole = newRole != GroupRoles.Member
            || currentRole == GroupRoles.Owner
            || currentRole == GroupRoles.Admin;
        if (touchesPrivilegedRole && callerRole != GroupRoles.Owner)
        {
            throw new GroupAccessDeniedException("Only a group owner (or global admin) can grant or change owner/admin roles");
        }

        // The last active owner can never be demoted - not even by a global admin.
        if (currentRole == GroupRoles.Owner && newRole != GroupRoles.Owner
            && !await HasAnotherActiveOwnerAsync(groupId, userId, ct))
        {
            throw new InvalidOperationException("Cannot demote the last owner of the group");
        }

        // Nor silently expired: re-granting owner to the last owner WITH an expiry would drop
        // the group to zero owners the moment the clock passes it (review finding SF-1 on
        // PR #138). Owner grants on the sole owner must be open-ended.
        if (currentRole == GroupRoles.Owner && newRole == GroupRoles.Owner && expiresAt != null
            && !await HasAnotherActiveOwnerAsync(groupId, userId, ct))
        {
            throw new InvalidOperationException("Cannot set an expiry on the last owner of the group");
        }

        if (existingMembership != null)
        {
            // Reactivate if previously removed / upsert the role
            existingMembership.IsActive = true;
            existingMembership.Role = newRole;
            existingMembership.ExpiresAt = expiresAt;
            existingMembership.JoinedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);
            return existingMembership;
        }

        var membership = new GroupMembership
        {
            GroupId = groupId,
            UserId = userId,
            Role = newRole,
            ExpiresAt = expiresAt
        };

        _context.GroupMemberships.Add(membership);
        await _context.SaveChangesAsync(ct);

        return membership;
    }

    public async Task<bool> RemoveMemberAsync(Guid groupId, Guid userId, GroupActor actor, CancellationToken ct = default)
    {
        var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group == null)
        {
            return false;
        }

        var membership = await _context.GroupMemberships
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId && m.IsActive, ct);

        if (membership == null)
        {
            return false;
        }

        var callerRole = await RequireGroupAuthorityAsync(group, actor, ownerOnly: false, ct);

        // Removing an owner or admin is owner-only (or global admin).
        var targetRole = GroupRoles.Normalize(membership.Role);
        if ((targetRole == GroupRoles.Owner || targetRole == GroupRoles.Admin) && callerRole != GroupRoles.Owner)
        {
            throw new GroupAccessDeniedException("Only a group owner (or global admin) can remove an owner or admin");
        }

        // The last active owner can never be removed - not even by a global admin.
        if (targetRole == GroupRoles.Owner && !await HasAnotherActiveOwnerAsync(groupId, userId, ct))
        {
            throw new InvalidOperationException("Cannot remove the last owner of the group");
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

    public async Task<GroupRole> AssignRoleToGroupAsync(Guid groupId, Guid roleId, Guid tenantId, GroupActor actor, CancellationToken ct = default)
    {
        // Attaching permission roles to a group is global-admin only: a group admin could
        // otherwise attach a full-access role to their own group (privilege escalation).
        if (!actor.IsGlobalAdmin)
        {
            throw new GroupAccessDeniedException("Attaching permission roles to a group requires a global admin");
        }

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
            AssignedByUserId = actor.UserId
        };

        _context.GroupRoles.Add(groupRole);
        await _context.SaveChangesAsync(ct);

        return groupRole;
    }

    public async Task<bool> RemoveRoleFromGroupAsync(Guid groupId, Guid roleId, GroupActor actor, CancellationToken ct = default)
    {
        var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group == null)
        {
            return false;
        }

        var groupRole = await _context.GroupRoles
            .FirstOrDefaultAsync(gr => gr.GroupId == groupId && gr.RoleId == roleId, ct);

        if (groupRole == null)
        {
            return false;
        }

        // Detaching a permission role reduces privilege, so group owners/admins may do it too.
        await RequireGroupAuthorityAsync(group, actor, ownerOnly: false, ct);

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

    public async Task<List<Group>> GetGroupsWithoutActiveOwnerAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _context.Groups
            .Include(g => g.Tenant)
            .Where(g => g.IsActive && !g.Members.Any(m => m.IsActive
                && (m.ExpiresAt == null || m.ExpiresAt > now)
                && m.Role.ToLower() == GroupRoles.Owner))
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
    }

    public async Task<GroupRoleBackfillResult> BackfillGroupRolesAsync(CancellationToken ct = default)
    {
        // Idempotent, additive-only: canonicalize casing/whitespace of role values that already
        // mean owner/admin/member. Existing owner/admin rows were never enforced and cannot be
        // trusted, so NOBODY is promoted here and NOTHING is removed; unknown role values are
        // left untouched (they confer no authority). Groups without an active owner are counted
        // and surfaced to global admins via GetGroupsWithoutActiveOwnerAsync.
        var denormalized = await _context.GroupMemberships
            .Where(m => m.Role != m.Role.Trim().ToLower())
            .ToListAsync(ct);

        var normalizedCount = 0;
        foreach (var membership in denormalized)
        {
            var canonical = GroupRoles.Normalize(membership.Role);
            if (canonical != null && !string.Equals(membership.Role, canonical, StringComparison.Ordinal))
            {
                membership.Role = canonical;
                normalizedCount++;
            }
        }

        if (normalizedCount > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        var ownerless = await _context.Groups
            .CountAsync(g => g.IsActive && !g.Members.Any(m => m.IsActive
                && (m.ExpiresAt == null || m.ExpiresAt > DateTime.UtcNow)
                && m.Role.ToLower() == GroupRoles.Owner), ct);

        return new GroupRoleBackfillResult(normalizedCount, ownerless);
    }

    /// <summary>
    /// The group role the actor's own active, non-expired membership grants - or null when the
    /// actor has none, or when the token's tenant_id claim points at a different tenant than the
    /// group's (a tenant-scoped token has no authority outside its tenant).
    /// </summary>
    private async Task<string?> GetActorGroupRoleAsync(Group group, GroupActor actor, CancellationToken ct)
    {
        if (actor.TenantId.HasValue && actor.TenantId.Value != group.TenantId)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var membership = await _context.GroupMemberships
            .FirstOrDefaultAsync(m => m.GroupId == group.Id
                && m.UserId == actor.UserId
                && m.IsActive
                && (m.ExpiresAt == null || m.ExpiresAt > now), ct);

        return membership == null ? null : GroupRoles.Normalize(membership.Role);
    }

    /// <summary>
    /// Throws <see cref="GroupAccessDeniedException"/> unless the actor is a global admin, the
    /// group's owner, or (when <paramref name="ownerOnly"/> is false) a group admin. Returns the
    /// effective role ("owner" for global admins) so callers can apply owner-only sub-rules.
    /// </summary>
    private async Task<string> RequireGroupAuthorityAsync(Group group, GroupActor actor, bool ownerOnly, CancellationToken ct)
    {
        if (actor.IsGlobalAdmin)
        {
            return GroupRoles.Owner;
        }

        var role = await GetActorGroupRoleAsync(group, actor, ct);
        if (role == GroupRoles.Owner || (!ownerOnly && role == GroupRoles.Admin))
        {
            return role;
        }

        throw new GroupAccessDeniedException(ownerOnly
            ? "Only a group owner (or global admin) can perform this action"
            : "Only a group owner or admin (or global admin) can perform this action");
    }

    private async Task<bool> HasAnotherActiveOwnerAsync(Guid groupId, Guid excludingUserId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await _context.GroupMemberships.AnyAsync(m => m.GroupId == groupId
            && m.UserId != excludingUserId
            && m.IsActive
            && (m.ExpiresAt == null || m.ExpiresAt > now)
            && m.Role.ToLower() == GroupRoles.Owner, ct);
    }
}
