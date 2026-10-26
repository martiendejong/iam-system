using IAM.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Data;

/// <summary>
/// The one place that ends refresh-token sessions (task 4709). Every route that deactivates,
/// locks or force-resets a user calls <see cref="RevokeRefreshTokensAsync(IAMDbContext, Guid, CancellationToken)"/>
/// instead of looping over <c>RefreshTokens</c> itself, so a new route cannot quietly forget it.
/// <para>
/// The revoke methods only stage the change on the context; the caller's own
/// <c>SaveChangesAsync</c> commits it together with the deactivation, so the two can never
/// disagree.
/// </para>
/// </summary>
public static class RefreshTokenRevocation
{
    /// <summary>Revokes every still-unrevoked refresh token of one user. Returns how many were revoked.</summary>
    public static Task<int> RevokeRefreshTokensAsync(this IAMDbContext context, Guid userId, CancellationToken ct = default)
        => context.RevokeRefreshTokensAsync(new[] { userId }, ct);

    /// <summary>
    /// Revokes every still-unrevoked refresh token of one user except <paramref name="exceptTokenId"/>: the session that
    /// is making the change (task 5157) and stays signed in. A null id keeps nothing. Returns how many were revoked.
    /// </summary>
    public static Task<int> RevokeRefreshTokensAsync(this IAMDbContext context, Guid userId, Guid? exceptTokenId, CancellationToken ct = default)
        => context.RevokeRefreshTokensAsync(new[] { userId }, ct, exceptTokenId);

    /// <summary>Revokes every still-unrevoked refresh token of the given users. Returns how many were revoked.</summary>
    public static async Task<int> RevokeRefreshTokensAsync(this IAMDbContext context, IEnumerable<Guid> userIds, CancellationToken ct = default, Guid? exceptTokenId = null)
    {
        var ids = userIds.Distinct().ToArray();
        var keep = exceptTokenId ?? Guid.Empty;
        if (ids.Length == 0)
        {
            return 0;
        }

        // Only tokens that are not revoked yet: an already-revoked token keeps its original
        // RevokedAt, which is what lets IsRotated tell a rotation apart from a bulk revoke.
        var tokens = await context.RefreshTokens
            .Where(rt => ids.Contains(rt.UserId) && rt.RevokedAt == null && rt.Id != keep)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
        }

        return tokens.Count;
    }

    /// <summary>
    /// Single-use rotation: revokes <paramref name="spent"/> and stamps its successor with the
    /// very same instant. That equality is the only record that a revocation was a rotation (as
    /// opposed to logout, password reset or deactivation), and it needs no extra column.
    /// </summary>
    public static void Rotate(RefreshToken spent, RefreshToken successor)
    {
        var now = DateTime.UtcNow;
        spent.RevokedAt = now;
        successor.CreatedAt = now;
    }

    /// <summary>
    /// True when <paramref name="token"/> was revoked by <see cref="Rotate"/>, i.e. its owner has a
    /// token created at exactly the moment this one was revoked. Presenting such a token again
    /// means two parties hold the same secret.
    /// </summary>
    public static Task<bool> IsRotatedAsync(this IAMDbContext context, RefreshToken token, CancellationToken ct = default)
    {
        if (token.RevokedAt is not { } revokedAt)
        {
            return Task.FromResult(false);
        }

        return context.RefreshTokens.AnyAsync(
            rt => rt.UserId == token.UserId && rt.Id != token.Id && rt.CreatedAt == revokedAt, ct);
    }
}
