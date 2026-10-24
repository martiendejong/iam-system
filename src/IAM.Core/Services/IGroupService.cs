using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IGroupService
{
    Task<Group> CreateGroupAsync(Group group, GroupActor actor, CancellationToken ct = default);
    Task<Group?> GetGroupAsync(Guid groupId, CancellationToken ct = default);
    Task<List<Group>> GetGroupsByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<Group> UpdateGroupAsync(Guid groupId, string name, string? description, string? metadata, GroupActor actor, CancellationToken ct = default);
    Task<bool> DeleteGroupAsync(Guid groupId, GroupActor actor, CancellationToken ct = default);
    Task<GroupMembership> AddMemberAsync(Guid groupId, Guid userId, GroupActor actor, string role = GroupRoles.Member, DateTime? expiresAt = null, CancellationToken ct = default);
    Task<bool> RemoveMemberAsync(Guid groupId, Guid userId, GroupActor actor, CancellationToken ct = default);
    Task<List<GroupMembership>> GetMembersAsync(Guid groupId, CancellationToken ct = default);
    Task<List<Group>> GetUserGroupsAsync(Guid userId, CancellationToken ct = default);
    Task<GroupRole> AssignRoleToGroupAsync(Guid groupId, Guid roleId, Guid tenantId, GroupActor actor, CancellationToken ct = default);
    Task<bool> RemoveRoleFromGroupAsync(Guid groupId, Guid roleId, GroupActor actor, CancellationToken ct = default);
    Task<List<string>> GetEffectivePermissionsAsync(Guid userId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Active groups that have no active, non-expired owner (surfaced to global admins; task 4056).
    /// </summary>
    Task<List<Group>> GetGroupsWithoutActiveOwnerAsync(CancellationToken ct = default);

    /// <summary>
    /// Idempotent startup backfill (task 4056): canonicalizes legacy role value casing/whitespace and
    /// counts ownerless groups. Removes nothing, promotes nobody.
    /// </summary>
    Task<GroupRoleBackfillResult> BackfillGroupRolesAsync(CancellationToken ct = default);
}
