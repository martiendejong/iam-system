using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// One step in the authority chain returned by the who-decides resolver (task 4059).
/// </summary>
/// <param name="PrincipalId">Database ID of this authority principal.</param>
/// <param name="DisplayName">Human-readable name (full name for users, Name for service accounts).</param>
/// <param name="Kind">
/// PrincipalKind string for users/service accounts ("Human"/"Agent"/"Service");
/// for group authorities this is the group name.
/// </param>
/// <param name="IamSubject">
/// Stable IAM-side identifier: "user:{email}", "sa:{clientId}", "group:{name}".
/// </param>
/// <param name="HopRole">
/// "manager" when the hop was reached via the manager link;
/// "group-owner" / "group-admin" for group-target chains.
/// </param>
/// <param name="TenantId">Primary tenant of this authority, when known.</param>
public sealed record AuthorityHop(
    Guid PrincipalId,
    string DisplayName,
    string Kind,
    string IamSubject,
    string HopRole,
    Guid? TenantId);

public enum ResolverStatus
{
    Success,
    NotFound,
    InvalidInput
}

public sealed record ResolverResult(
    ResolverStatus Status,
    IReadOnlyList<AuthorityHop>? Chain = null,
    bool TruncatedAt8Hops = false,
    string? Error = null)
{
    public static ResolverResult Ok(IReadOnlyList<AuthorityHop> chain, bool truncated = false)
        => new(ResolverStatus.Success, chain, truncated);

    public static ResolverResult NotFound(string? error = null)
        => new(ResolverStatus.NotFound, Error: error);

    public static ResolverResult Invalid(string error)
        => new(ResolverStatus.InvalidInput, Error: error);
}

/// <summary>
/// Resolves the ordered authority chain for a principal (task 4059).
///
/// For users and service accounts: walks the manager chain up to 8 hops, skipping
/// disabled managers, stopping at tenant boundaries.
///
/// For groups: returns owners first, then admins (consumed by access-request T11).
///
/// This service is called only by the vault's confidential client
/// (scope "iam_resolver", client_credentials). It is never called by user tokens.
/// </summary>
public interface IResolverService
{
    /// <summary>
    /// Returns the ordered authority chain for a user principal.
    /// The subject user is NOT included in the chain; the chain starts with their direct manager.
    /// </summary>
    Task<ResolverResult> ResolveUserChainAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the ordered authority chain for a service-account principal.
    /// The chain starts with the service account's direct manager (always a User).
    /// </summary>
    Task<ResolverResult> ResolveServiceAccountChainAsync(Guid serviceAccountId, CancellationToken ct = default);

    /// <summary>
    /// For a group target: returns active owners first, then active admins.
    /// Consumed by access-request flow T11.
    /// </summary>
    Task<ResolverResult> ResolveGroupChainAsync(Guid groupId, CancellationToken ct = default);
}
