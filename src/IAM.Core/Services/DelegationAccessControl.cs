namespace IAM.Core.Services;

/// <summary>
/// The authenticated caller on whose behalf a delegation operation runs (task 4712). Same shape as
/// <see cref="GroupActor"/> / <see cref="IdentityProviderActor"/>.
/// </summary>
/// <param name="UserId">The caller's user id (token subject).</param>
/// <param name="IsGlobalAdmin">True when the caller holds the SuperAdmin or SystemAdmin role.</param>
/// <param name="TenantId">
/// The tenant_id claim from the caller's token, when present. Password-login tokens carry no
/// tenant_id; a token that does carry one confers no authority in any other tenant.
/// </param>
public sealed record DelegationActor(Guid UserId, bool IsGlobalAdmin, Guid? TenantId);

/// <summary>
/// Thrown when a <see cref="DelegationActor"/> lacks the authority a delegation operation requires.
/// Controllers map this to HTTP 403.
/// </summary>
public class DelegationAccessDeniedException : Exception
{
    public DelegationAccessDeniedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Permission-string rules for delegations. A delegator can only hand out what they hold, and a
/// delegation that grants blanket (wildcard) authority is never self-service.
/// </summary>
public static class DelegationPermissions
{
    /// <summary>True when the permission is a wildcard pattern such as "*" or "Building.*".</summary>
    public static bool IsWildcard(string permission) => permission.EndsWith('*');

    /// <summary>
    /// True when <paramref name="held"/> covers <paramref name="requested"/>: an exact match, or a held
    /// wildcard whose prefix the requested permission starts with ("*" covers everything). A requested
    /// wildcard is therefore only covered by an equal or broader held wildcard, never by a narrower one.
    /// </summary>
    public static bool Covers(IEnumerable<string> held, string requested)
    {
        foreach (var heldPermission in held)
        {
            if (string.Equals(heldPermission, requested, StringComparison.Ordinal))
                return true;

            if (IsWildcard(heldPermission)
                && requested.StartsWith(heldPermission[..^1], StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
