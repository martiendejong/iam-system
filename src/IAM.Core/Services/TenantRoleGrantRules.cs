using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Who is handing out a role inside one tenant (task 4700): a SuperAdmin, and/or a BuildingOwner of that tenant.
/// A BuildingManager of the tenant is the default (both false).
/// </summary>
public sealed record TenantGrantor(bool IsSuperAdmin, bool IsTenantOwner)
{
    public static TenantGrantor Manager { get; } = new(false, false);
    public static TenantGrantor Owner { get; } = new(false, true);
    public static TenantGrantor SuperAdmin { get; } = new(true, true);
}

/// <summary>Why a role may not be granted by a tenant manager.</summary>
public enum RoleGrantProblem
{
    None,

    /// <summary>A platform-wide role (SuperAdmin, SystemAdmin, ...): SuperAdmin only.</summary>
    PlatformRole,

    /// <summary>An owner-level role (BuildingOwner, TenantAdmin, OrganizationOwner): SuperAdmin or an owner of the tenant.</summary>
    OwnerLevelRole,

    /// <summary>A custom role owned by a different tenant.</summary>
    OtherTenantRole
}

/// <summary>
/// The one rule for which roles a caller may hand out in a tenant, by role change (TenantsController),
/// invitation (InvitationsController, bulk invites) and invitation acceptance (task 4700). Decided on the role
/// NAME, never on Role.TenantId, so a tenant cannot create a custom role called "SystemAdmin" to smuggle it in.
/// </summary>
public static class TenantRoleGrantRules
{
    /// <summary>Platform-wide roles: only a SuperAdmin may grant them.</summary>
    public static readonly IReadOnlySet<string> PlatformRoleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SuperAdmin",
        "SystemAdmin",
        "SecurityAdmin",
        "ComplianceOfficer",
        "EmergencyAccess",
        "Admin",
    };

    /// <summary>
    /// Roles above manager level inside a tenant: granted by a SuperAdmin or an owner of that tenant, never by a
    /// manager. TenantAdmin and OrganizationOwner are included because each unlocks tenant administration
    /// (identity providers, settings, webhooks) that a manager must not hand to themselves or others.
    /// </summary>
    public static readonly IReadOnlySet<string> OwnerLevelRoleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BuildingOwner",
        "TenantAdmin",
        "OrganizationOwner",
    };

    public static bool IsPlatformRole(string? roleName) => roleName != null && PlatformRoleNames.Contains(roleName.Trim());

    public static bool IsOwnerLevelRole(string? roleName) => roleName != null && OwnerLevelRoleNames.Contains(roleName.Trim());

    public static RoleGrantProblem Check(Role role, Guid tenantId, TenantGrantor grantor)
    {
        if (grantor.IsSuperAdmin)
            return RoleGrantProblem.None;

        if (IsPlatformRole(role.Name))
            return RoleGrantProblem.PlatformRole;

        if (IsOwnerLevelRole(role.Name) && !grantor.IsTenantOwner)
            return RoleGrantProblem.OwnerLevelRole;

        if (role.TenantId.HasValue && role.TenantId.Value != tenantId)
            return RoleGrantProblem.OtherTenantRole;

        return RoleGrantProblem.None;
    }

    /// <summary>Plain-language message for a refused grant.</summary>
    public static string Describe(RoleGrantProblem problem, string roleName) => problem switch
    {
        RoleGrantProblem.PlatformRole => $"Role '{roleName}' is a platform-wide role and can only be granted by a SuperAdmin",
        RoleGrantProblem.OwnerLevelRole => $"Role '{roleName}' can only be granted by a SuperAdmin or an owner of this tenant",
        RoleGrantProblem.OtherTenantRole => $"Role '{roleName}' belongs to a different tenant",
        _ => string.Empty
    };
}
