using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

/// <summary>
/// "Is this user an admin of that tenant?", answered from the UserRoles table (tasks 4697, 4738).
/// A user is a tenant admin when they hold an active (not expired) TenantAdmin role row scoped to the
/// tenant; a TenantAdmin row without a tenant confers nothing, and role claims in the token are never
/// consulted because they are global names, not tenant-scoped.
/// </summary>
public static class TenantAdminAuthority
{
    /// <summary>Role name that makes a user an admin of the tenant its UserRole row is scoped to.</summary>
    public const string RoleName = "TenantAdmin";

    /// <summary>True when the user holds an active TenantAdmin role scoped to exactly <paramref name="tenantId"/>.</summary>
    public static Task<bool> IsAdminOfTenantAsync(IAMDbContext context, Guid userId, Guid tenantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return context.UserRoles.AnyAsync(ur =>
            ur.UserId == userId
            && ur.TenantId == tenantId
            && ur.Role.Name == RoleName
            && (ur.ExpiresAt == null || ur.ExpiresAt > now), ct);
    }

    /// <summary>
    /// True when the user holds an active TenantAdmin role in at least one tenant - only in
    /// <paramref name="onlyTenantId"/> when given (the tenant_id claim of a tenant-scoped token).
    /// </summary>
    public static Task<bool> IsAdminOfAnyTenantAsync(IAMDbContext context, Guid userId, Guid? onlyTenantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = context.UserRoles.Where(ur =>
            ur.UserId == userId
            && ur.TenantId != null
            && ur.Role.Name == RoleName
            && (ur.ExpiresAt == null || ur.ExpiresAt > now));

        if (onlyTenantId.HasValue)
            query = query.Where(ur => ur.TenantId == onlyTenantId.Value);

        return query.AnyAsync(ct);
    }
}
