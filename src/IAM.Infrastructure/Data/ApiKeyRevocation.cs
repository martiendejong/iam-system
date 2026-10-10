using Hazina.Security.ApiKeys;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Data;

/// <summary>
/// The one place that revokes the API keys a user owns (task 5155). Every route that deactivates,
/// locks, merges away or erases a user calls <see cref="RevokeApiKeysAsync(IAMDbContext, Guid, CancellationToken)"/>
/// next to <see cref="RefreshTokenRevocation.RevokeRefreshTokensAsync(IAMDbContext, Guid, CancellationToken)"/>,
/// so reactivating the user later cannot bring an old key back to life.
/// <para>
/// The key store already refuses a key whose owner is inactive (<c>EfApiKeyStore</c>); this makes the
/// revocation permanent and visible in the key list. Like the refresh-token helper it only stages the
/// change: the caller's own <c>SaveChangesAsync</c> commits it together with the deactivation. After that
/// save the caller passes the returned hashes to <see cref="Forget"/>, because the shared key module caches
/// validated keys for a couple of minutes.
/// </para>
/// </summary>
public static class ApiKeyRevocation
{
    /// <summary>Revokes every still-active API key the user owns. Returns the hashes of the keys that were revoked.</summary>
    public static Task<IReadOnlyList<string>> RevokeApiKeysAsync(this IAMDbContext context, Guid userId, CancellationToken ct = default)
        => context.RevokeApiKeysAsync(new[] { userId }, ct);

    /// <summary>Revokes every still-active API key the given users own. Returns the hashes of the keys that were revoked.</summary>
    public static async Task<IReadOnlyList<string>> RevokeApiKeysAsync(this IAMDbContext context, IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return Array.Empty<string>();
        }

        // Keys without an owner (service / tenant keys) never match: they are not tied to a person.
        var keys = await context.ApiKeys
            .Where(k => k.IsActive && k.UserId.HasValue && ids.Contains(k.UserId.Value))
            .ToListAsync(ct);

        foreach (var key in keys)
        {
            key.IsActive = false;
        }

        return keys.Select(k => k.KeyHash).ToList();
    }

    /// <summary>
    /// Drops the revoked keys from the key module's in-process cache so the revocation applies to the very next
    /// request instead of after the cache lifetime. Call it after the <c>SaveChangesAsync</c> that committed the
    /// revocation (invalidating earlier would let a concurrent request re-cache the still-active row).
    /// A null cache (a service built without DI) is a no-op: the cache lifetime then bounds the delay.
    /// </summary>
    public static void Forget(this IApiKeyCache? cache, IEnumerable<string> keyHashes)
    {
        if (cache is null)
        {
            return;
        }

        foreach (var hash in keyHashes)
        {
            cache.Invalidate(hash);
        }
    }
}
