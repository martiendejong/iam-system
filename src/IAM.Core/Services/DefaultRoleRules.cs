using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>Why a role cannot be the automatic default role of a tenant-scoped feature.</summary>
public enum DefaultRoleProblem
{
    /// <summary>The role is usable as a default role.</summary>
    None,

    /// <summary>No role with that id exists.</summary>
    NotFound,

    /// <summary>The role is privileged (<see cref="PrivilegedRoles.IsPrivileged"/>) and must never be handed out automatically.</summary>
    Privileged,

    /// <summary>The role is owned by a different tenant than the one it would be used in.</summary>
    OtherTenant
}

/// <summary>
/// The one rule for a "default role" that gets assigned without a human choosing it (an identity
/// provider's auto-created users, an organization's role-less bulk invitations): it must exist,
/// be non-privileged, and be global or owned by the tenant it is used in (tasks 4697, 4738).
/// </summary>
public static class DefaultRoleRules
{
    /// <param name="role">The role as stored, or null when the id matched nothing.</param>
    /// <param name="tenantId">The tenant the role would be used in (null = platform-wide).</param>
    public static DefaultRoleProblem Check(Role? role, Guid? tenantId)
    {
        if (role == null)
            return DefaultRoleProblem.NotFound;

        if (PrivilegedRoles.IsPrivileged(role))
            return DefaultRoleProblem.Privileged;

        if (role.TenantId.HasValue && role.TenantId != tenantId)
            return DefaultRoleProblem.OtherTenant;

        return DefaultRoleProblem.None;
    }
}
