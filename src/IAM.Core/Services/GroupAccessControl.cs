namespace IAM.Core.Services;

/// <summary>
/// The authenticated caller on whose behalf a group mutation runs (task 4056).
/// </summary>
/// <param name="UserId">The caller's user id (token subject).</param>
/// <param name="IsGlobalAdmin">True when the caller is a global admin (SuperAdmin role).</param>
/// <param name="TenantId">
/// The tenant_id claim from the caller's token, when present. Password-login tokens carry no
/// tenant_id; tokens that do carry one are scoped to that tenant and confer no group authority
/// in any other tenant.
/// </param>
public sealed record GroupActor(Guid UserId, bool IsGlobalAdmin, Guid? TenantId);

/// <summary>
/// Thrown when a <see cref="GroupActor"/> lacks the group authority an operation requires.
/// Controllers map this to HTTP 403.
/// </summary>
public class GroupAccessDeniedException : Exception
{
    public GroupAccessDeniedException(string message) : base(message)
    {
    }
}

/// <summary>
/// The closed set of roles a user can hold inside a group.
/// </summary>
public static class GroupRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    /// <summary>
    /// Canonicalizes a role value (trim + lowercase). Returns null when the value is not one of
    /// the valid roles, so legacy/garbage values simply confer no authority.
    /// </summary>
    public static string? Normalize(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        var normalized = role.Trim().ToLowerInvariant();
        return normalized is Owner or Admin or Member ? normalized : null;
    }
}

/// <summary>
/// Outcome of the idempotent group-role backfill that runs at startup (task 4056).
/// The backfill never removes memberships and never promotes anyone.
/// </summary>
/// <param name="NormalizedRoleValues">Membership rows whose role value was canonicalized (casing/whitespace only).</param>
/// <param name="GroupsWithoutActiveOwner">Active groups that currently have no active, non-expired owner.</param>
public sealed record GroupRoleBackfillResult(int NormalizedRoleValues, int GroupsWithoutActiveOwner);
