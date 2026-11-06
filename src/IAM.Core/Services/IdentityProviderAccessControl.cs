using System.Text.Json;
using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// The authenticated caller on whose behalf an identity-provider mutation runs (task 4697).
/// Same shape as <see cref="GroupActor"/>.
/// </summary>
/// <param name="UserId">The caller's user id (token subject).</param>
/// <param name="IsGlobalAdmin">True when the caller holds the SuperAdmin or SystemAdmin role.</param>
/// <param name="TenantId">
/// The tenant_id claim from the caller's token, when present. Password-login tokens carry no
/// tenant_id; a token that does carry one confers no authority over another tenant's providers.
/// </param>
public sealed record IdentityProviderActor(Guid UserId, bool IsGlobalAdmin, Guid? TenantId);

/// <summary>
/// Thrown when an <see cref="IdentityProviderActor"/> may not perform the requested identity-provider
/// operation. Controllers map this to HTTP 403.
/// </summary>
public class IdentityProviderAccessDeniedException : Exception
{
    public IdentityProviderAccessDeniedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when an identity-provider payload is invalid (unknown tenant, unknown or privileged
/// default role). Controllers map this to HTTP 400.
/// </summary>
public class IdentityProviderValidationException : Exception
{
    public IdentityProviderValidationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Which roles may never be handed out automatically (e.g. as the default role of an identity
/// provider's auto-created users). A role is privileged when it is one the API treats as an
/// administrative/management role, when its name says so, or when it grants a wildcard permission.
/// </summary>
public static class PrivilegedRoles
{
    /// <summary>
    /// Every role name the API authorizes on (<c>[Authorize(Roles = ...)]</c> / <c>IsInRole</c>),
    /// plus the self-service OrganizationOwner role and the legacy "Admin" name the seeder still
    /// recognizes. A test keeps this set in sync with the attributes in the API assembly.
    /// </summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SuperAdmin",
        "SystemAdmin",
        "SecurityAdmin",
        "ComplianceOfficer",
        "TenantAdmin",
        "EmergencyAccess",
        "BuildingOwner",
        "BuildingManager",
        "OrganizationOwner",
        "Admin",
    };

    /// <summary>
    /// True when <paramref name="role"/> must not be assigned automatically. Fails closed: a role
    /// whose permission list cannot be read is treated as privileged.
    /// </summary>
    public static bool IsPrivileged(Role role)
    {
        var name = role.Name?.Trim() ?? string.Empty;
        if (Names.Contains(name) || name.Contains("admin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return GrantsWildcardPermission(role.Permissions);
    }

    private static bool GrantsWildcardPermission(string? permissionsJson)
    {
        if (string.IsNullOrWhiteSpace(permissionsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(permissionsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            foreach (var permission in document.RootElement.EnumerateArray())
            {
                var value = permission.ValueKind == JsonValueKind.String ? permission.GetString()?.Trim() : null;
                if (value == null || value.EndsWith('*'))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
