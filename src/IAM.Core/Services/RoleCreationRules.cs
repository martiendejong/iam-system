namespace IAM.Core.Services;

/// <summary>Why a tenant owner (BuildingOwner) may not create the requested role (task 5163).</summary>
public enum RoleCreateProblem
{
    None,

    /// <summary>No tenant in the request: that would be a global role, which only a SuperAdmin may create.</summary>
    TenantRequired,

    /// <summary>The tenant is not one the caller owns.</summary>
    OtherTenant,

    /// <summary>The category is "app:{clientId}": it turns that application's sign-in into a role-gated one for everybody.</summary>
    AppCategory,

    /// <summary>A permission contains a wildcard ("*", "users.*", ...).</summary>
    WildcardPermission,

    /// <summary>The name is a platform-wide role name (SuperAdmin, SystemAdmin, ...): the token's role claim is the role NAME.</summary>
    PlatformRoleName
}

/// <summary>
/// The one rule for what a BuildingOwner may put in <c>POST /api/roles</c>. A SuperAdmin is not checked at all. Role
/// claims and the app-role gate (AuthorizationController.EvaluateAppRoleGateAsync) are decided on a role's name and
/// category, so those two fields and the tenant must never be taken on trust from the request body.
/// </summary>
public static class RoleCreationRules
{
    /// <summary>A role with this category prefix is part of an application's role catalog ("app:{clientId}").</summary>
    public const string AppCategoryPrefix = "app:";

    public static bool IsAppCategory(string? category) =>
        category != null && category.Trim().StartsWith(AppCategoryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Any "*" in a permission counts, wherever it sits ("*", "users.*", "*.read").</summary>
    public static bool HasWildcardPermission(IEnumerable<string?>? permissions) =>
        permissions != null && permissions.Any(p => p != null && p.Contains('*'));

    /// <param name="name">The requested role name.</param>
    /// <param name="tenantId">The tenant the request asks the role to belong to (null = global).</param>
    /// <param name="category">The requested category.</param>
    /// <param name="permissions">The requested permissions.</param>
    /// <param name="ownedTenants">The tenants the caller owns, taken from the caller's own tenant roles, never from the request.</param>
    public static RoleCreateProblem CheckForTenantOwner(
        string? name,
        Guid? tenantId,
        string? category,
        IEnumerable<string?>? permissions,
        IReadOnlySet<Guid> ownedTenants)
    {
        if (!tenantId.HasValue)
            return RoleCreateProblem.TenantRequired;

        if (!ownedTenants.Contains(tenantId.Value))
            return RoleCreateProblem.OtherTenant;

        if (IsAppCategory(category))
            return RoleCreateProblem.AppCategory;

        if (HasWildcardPermission(permissions))
            return RoleCreateProblem.WildcardPermission;

        if (TenantRoleGrantRules.IsPlatformRole(name))
            return RoleCreateProblem.PlatformRoleName;

        return RoleCreateProblem.None;
    }

    /// <summary>Plain-language message for a refused creation.</summary>
    public static string Describe(RoleCreateProblem problem) => problem switch
    {
        RoleCreateProblem.TenantRequired => "A tenant is required: only a SuperAdmin can create a role that belongs to no tenant",
        RoleCreateProblem.OtherTenant => "You can only create roles in a tenant you own",
        RoleCreateProblem.AppCategory => "Only a SuperAdmin can create a role with an application ('app:') category",
        RoleCreateProblem.WildcardPermission => "Only a SuperAdmin can create a role with wildcard permissions",
        RoleCreateProblem.PlatformRoleName => "This name belongs to a platform-wide role and can only be used by a SuperAdmin",
        _ => string.Empty
    };
}
