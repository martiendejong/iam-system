namespace IAM.Core.Services;

/// <summary>
/// The one tenant network check every sign-in and token-issuance path calls (task 5570).
/// Only the tenant IP allowlist is enforced. Country restrictions and geofences are NOT enforced
/// yet: there is no GeoIP source, so the lookup behind them returns nothing for real addresses.
/// </summary>
public interface ISignInNetworkGate
{
    /// <summary>Generic text for every refusal; it never says which tenant or which rule refused.</summary>
    public const string RefusalMessage = "Sign-in is not allowed from this network.";

    /// <summary>
    /// True when <paramref name="userId"/> may sign in or renew a token from <paramref name="ipAddress"/>.
    /// With <paramref name="tenantId"/> null the address must satisfy the allowlist of EVERY tenant where
    /// the user holds an active role (a user in several tenants is bound by all of their restrictions);
    /// with a tenant (the token's tenant on refresh) only that tenant's list applies. A tenant without
    /// active allowlist entries imposes nothing. A missing or unparseable address never matches an
    /// active list. A refusal writes one BlockedIpLog row per refusing tenant and one audit event.
    /// </summary>
    Task<bool> IsAllowedAsync(Guid userId, string? ipAddress, string? userAgent = null, Guid? tenantId = null, CancellationToken cancellationToken = default);
}
