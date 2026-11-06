using System.Security.Claims;
using Hazina.Security.ApiKeys;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Authorization;

/// <summary>
/// Which tenants a caller may read and which it may change (task 4726). Built once per request by
/// <see cref="ITenantAccessResolver"/>; the endpoint then asks <see cref="CanRead"/> / <see cref="CanManage"/> with the
/// tenant of the stored resource, never with a tenant taken from the request body.
/// </summary>
public sealed class TenantAccess
{
    private readonly HashSet<Guid> _readable;
    private readonly HashSet<Guid> _manageable;

    private TenantAccess(bool refused, bool allTenants, IEnumerable<Guid> readable, IEnumerable<Guid> manageable)
    {
        IsRefused = refused;
        AllTenants = allTenants;
        _readable = readable.ToHashSet();
        _manageable = manageable.ToHashSet();
    }

    /// <summary>Device and service-account tokens (and tokens without a usable identity): 403 on every action.</summary>
    public bool IsRefused { get; }

    /// <summary>True for SuperAdmin and platform admin-scope API keys: every tenant, read and change.</summary>
    public bool AllTenants { get; }

    /// <summary>
    /// The tenants the caller may read, to pin a list query to; null means every tenant. Empty when the caller
    /// belongs to none (a list then shows nothing).
    /// </summary>
    public IReadOnlyCollection<Guid>? ReadableTenants => AllTenants ? null : _readable;

    /// <summary>
    /// The tenants the caller may change, to pin a query to (task 5163); null means every tenant. Empty when the
    /// caller manages none.
    /// </summary>
    public IReadOnlyCollection<Guid>? ManageableTenants => AllTenants ? null : _manageable;

    public bool CanRead(Guid tenantId) => !IsRefused && (AllTenants || _readable.Contains(tenantId));

    public bool CanManage(Guid tenantId) => !IsRefused && (AllTenants || _manageable.Contains(tenantId));

    /// <summary>
    /// True when the caller can change resources of at least one tenant. Checked before a lookup by id so a caller
    /// who manages nothing gets 403 for any id instead of learning which ids exist.
    /// </summary>
    public bool CanManageAny => !IsRefused && (AllTenants || _manageable.Count > 0);

    public static TenantAccess Refuse() => new(true, false, Array.Empty<Guid>(), Array.Empty<Guid>());

    public static TenantAccess Everything() => new(false, true, Array.Empty<Guid>(), Array.Empty<Guid>());

    public static TenantAccess ForTenants(IEnumerable<Guid> readable, IEnumerable<Guid> manageable) =>
        new(false, false, readable, manageable);
}

/// <summary>
/// The one tenant authorizer for resources that belong to a tenant but are reached through a role-less global
/// endpoint (devices today; the telemetry hub and REST API of task 4708 are meant to reuse it). Role claims are
/// global names and password-login tokens carry no tenant_id, so a user's tenants come from their UserRoles rows.
/// </summary>
public interface ITenantAccessResolver
{
    Task<TenantAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

public class TenantAccessResolver : ITenantAccessResolver
{
    private readonly IAMDbContext _context;

    public TenantAccessResolver(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<TenantAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        // API keys first: a key issued by a user also carries that user's id as NameIdentifier, but it is a
        // credential of its own with its own scope and tenant, not the user's roles.
        if (user.IsApiKey())
            return ResolveApiKey(user);

        // Device and service-account tokens are never tenant administrators or members.
        if (user.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null)
            return TenantAccess.Refuse();

        if (user.IsInRole("SuperAdmin"))
            return TenantAccess.Everything();

        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return TenantAccess.Refuse();

        // Null-tenant rows (a "global" role) deliberately do not count as membership of every tenant.
        var now = DateTime.UtcNow;
        var memberships = await _context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId
                && ur.TenantId != null
                && (ur.ExpiresAt == null || ur.ExpiresAt > now))
            .Select(ur => new { TenantId = ur.TenantId!.Value, RoleName = ur.Role.Name })
            .ToListAsync(cancellationToken);

        // A tenant-scoped token (tenant_id claim present) only carries authority in that tenant.
        var tenantClaim = user.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var claimedTenantId))
                return TenantAccess.Refuse();

            memberships = memberships.Where(m => m.TenantId == claimedTenantId).ToList();
        }

        var manageable = memberships
            .Where(m => AuditAccessResolver.TenantAdminRoles.Contains(m.RoleName))
            .Select(m => m.TenantId);

        return TenantAccess.ForTenants(memberships.Select(m => m.TenantId), manageable);
    }

    /// <summary>
    /// Read scope reads, write scope changes (admin implies both), always inside the tenant the key reaches:
    /// a tenant key reaches its own tenant, a platform key only reaches tenants with admin scope
    /// (<see cref="ApiKeyPrincipalExtensions.CanAccessTenant"/> is the single place that rule lives).
    /// </summary>
    private static TenantAccess ResolveApiKey(ClaimsPrincipal user)
    {
        if (user.GetApiKeyScope() is not { } scope)
            return TenantAccess.Refuse();

        var canChange = scope.Satisfies(ApiKeyScope.Write);

        var ownTenant = user.GetApiKeyTenantId();
        if (!string.IsNullOrEmpty(ownTenant))
        {
            if (!Guid.TryParse(ownTenant, out var tenantId) || !user.CanAccessTenant(ownTenant))
                return TenantAccess.Refuse();

            return TenantAccess.ForTenants(new[] { tenantId }, canChange ? new[] { tenantId } : Array.Empty<Guid>());
        }

        // Platform key: only admin scope reaches tenant-addressed resources.
        if (user.IsPlatformKey() && scope == ApiKeyScope.Admin)
            return TenantAccess.Everything();

        return TenantAccess.ForTenants(Array.Empty<Guid>(), Array.Empty<Guid>());
    }
}
