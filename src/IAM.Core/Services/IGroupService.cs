using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IGroupService
{
    Task<Group> CreateGroupAsync(Group group, CancellationToken ct = default);
    Task<Group?> GetGroupAsync(Guid groupId, CancellationToken ct = default);
    Task<List<Group>> GetGroupsByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<Group> UpdateGroupAsync(Guid groupId, string name, string? description, string? metadata, CancellationToken ct = default);
    Task<bool> DeleteGroupAsync(Guid groupId, CancellationToken ct = default);
    Task<GroupMembership> AddMemberAsync(Guid groupId, Guid userId, string role = "member", DateTime? expiresAt = null, CancellationToken ct = default);
    Task<bool> RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default);
    Task<List<GroupMembership>> GetMembersAsync(Guid groupId, CancellationToken ct = default);
    Task<List<Group>> GetUserGroupsAsync(Guid userId, CancellationToken ct = default);
    Task<GroupRole> AssignRoleToGroupAsync(Guid groupId, Guid roleId, Guid tenantId, Guid? assignedByUserId = null, CancellationToken ct = default);
    Task<bool> RemoveRoleFromGroupAsync(Guid groupId, Guid roleId, CancellationToken ct = default);
    Task<List<string>> GetEffectivePermissionsAsync(Guid userId, Guid tenantId, CancellationToken ct = default);
}
