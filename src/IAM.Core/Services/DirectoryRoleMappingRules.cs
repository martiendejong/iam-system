using IAM.Core.Entities;

namespace IAM.Core.Services;

public enum DirectoryRoleProblem
{
    None,
    NotFound,

    /// <summary>A platform-wide role (SuperAdmin, SystemAdmin, ...): a directory group can never grant it.</summary>
    PlatformRole,

    /// <summary>A custom role owned by a different tenant.</summary>
    OtherTenantRole
}

/// <summary>
/// Which roles a directory group may be mapped to (task 4698): only tenant roles - never a platform-wide one, never
/// another tenant's role. Decided on the role NAME, so a custom role called "SystemAdmin" is refused too.
/// </summary>
public static class DirectoryRoleMappingRules
{
    // Role claims carry the role NAME only, so a name that unlocks an [Authorize(Roles = ...)] endpoint unlocks it
    // platform-wide whatever tenant the UserRole row names. "TenantAdmin" is included for that reason: it gates
    // the temporal-access grant endpoints, which take any tenant id from the body. Only the tenant roles
    // (BuildingOwner, BuildingManager) are left grantable; a test keeps this set in step with the API's attributes.
    public static readonly IReadOnlySet<string> PlatformRoleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SuperAdmin",
        "SystemAdmin",
        "SecurityAdmin",
        "ComplianceOfficer",
        "EmergencyAccess",
        "TenantAdmin",
        "Admin",
    };

    /// <summary>The roles the API authorizes on that a directory group may still be mapped to: the tenant's own roles.</summary>
    public static readonly IReadOnlySet<string> GrantableTenantRoleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BuildingOwner",
        "BuildingManager",
    };

    public static DirectoryRoleProblem Check(Role? role, Guid tenantId)
    {
        if (role == null)
            return DirectoryRoleProblem.NotFound;

        if (PlatformRoleNames.Contains((role.Name ?? string.Empty).Trim()))
            return DirectoryRoleProblem.PlatformRole;

        if (role.TenantId.HasValue && role.TenantId.Value != tenantId)
            return DirectoryRoleProblem.OtherTenantRole;

        return DirectoryRoleProblem.None;
    }
}
