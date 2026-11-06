using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Task 5569: the single answer to "must this user complete a second factor to sign in?".
/// Every sign-in route (password, e-mail/SMS code, magic link, passkey, social, the OIDC
/// authorize/token endpoints and the refresh paths) asks this one question instead of
/// re-implementing it, so a route cannot forget the organization setting.
/// A user is held to the strictest organization: one membership in an organization with
/// OrganizationSettings.RequireMfa switched on is enough. Memberships whose role assignment
/// has expired do not count, the same rule that decides which roles reach a token.
/// </summary>
public static class OrganizationMfaPolicy
{
    /// <summary>Claim carried by session cookies and access/id tokens once a second factor was completed.</summary>
    public const string AuthenticationMethodsClaimType = "amr";

    /// <summary>Value of the <see cref="AuthenticationMethodsClaimType"/> claim for a completed second factor.</summary>
    public const string MultiFactorValue = "mfa";

    /// <summary>Session-cookie claim (IAM.Session) recording that this sign-in completed a second factor.</summary>
    public const string SessionMfaClaimType = "iam_mfa_completed";

    public static Task<bool> IsMfaRequiredAsync(IAMDbContext context, Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return context.UserRoles
            .Where(ur => ur.UserId == userId && ur.TenantId != null)
            .WhereActive(now)
            .AnyAsync(ur => context.Set<OrganizationSettings>()
                .Any(os => os.TenantId == ur.TenantId && os.RequireMfa), ct);
    }
}
