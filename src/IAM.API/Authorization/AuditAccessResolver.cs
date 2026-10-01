using System.Security.Claims;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Authorization;

public enum AuditAccessDecision
{
    Allowed,
    Forbidden,
    TenantRequired
}

/// <summary>
/// Outcome of <see cref="IAuditAccessResolver.ResolveAsync"/>. When <see cref="Decision"/> is
/// <see cref="AuditAccessDecision.Allowed"/>, <see cref="TenantId"/> is the tenant the query must be
/// limited to; null (platform roles only) means all tenants.
/// </summary>
public sealed record AuditAccessResult(AuditAccessDecision Decision, Guid? TenantId, string? Message)
{
    public static AuditAccessResult Allow(Guid? tenantId) => new(AuditAccessDecision.Allowed, tenantId, null);
    public static AuditAccessResult Forbid(string message) => new(AuditAccessDecision.Forbidden, null, message);
    public static AuditAccessResult NeedTenant(string message) => new(AuditAccessDecision.TenantRequired, null, message);
}

/// <summary>
/// Decides who may read audit events, compliance statistics and reports, and which tenant the
/// query is pinned to (task 4714). Audit rows carry user ids, policy changes and authorization
/// decisions, so they are admin-only and tenant-scoped.
/// </summary>
public interface IAuditAccessResolver
{
    Task<AuditAccessResult> ResolveAsync(ClaimsPrincipal user, Guid? requestedTenantId, CancellationToken cancellationToken);
}

public class AuditAccessResolver : IAuditAccessResolver
{
    /// <summary>Platform-level roles: may query any tenant, or all tenants at once.</summary>
    private static readonly string[] PlatformRoles = { "SuperAdmin", "SecurityAdmin" };

    /// <summary>
    /// Roles that make someone an administrator of a tenant. Same set the tenant-management
    /// endpoints use (TenantsController, InvitationsController: BuildingOwner/BuildingManager)
    /// plus TenantAdmin (TemporalPolicyController).
    /// </summary>
    private static readonly string[] TenantAdminRoles = { "TenantAdmin", "BuildingOwner", "BuildingManager" };

    private readonly IAMDbContext _context;

    public AuditAccessResolver(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<AuditAccessResult> ResolveAsync(
        ClaimsPrincipal user,
        Guid? requestedTenantId,
        CancellationToken cancellationToken)
    {
        // Device and service-account tokens are never audit readers.
        if (user.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null)
            return AuditAccessResult.Forbid("Audit data is only available to administrators.");

        if (PlatformRoles.Any(user.IsInRole))
            return AuditAccessResult.Allow(requestedTenantId);

        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return AuditAccessResult.Forbid("Audit data is only available to administrators.");

        // Role claims are global names, and password-login tokens carry no tenant_id, so the
        // tenant a caller administers comes from their UserRoles rows (null-tenant rows do not
        // count), exactly like GroupService / TenantBrandingController.
        var now = DateTime.UtcNow;
        var administeredTenants = await _context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId
                && ur.TenantId != null
                && (ur.ExpiresAt == null || ur.ExpiresAt > now)
                && TenantAdminRoles.Contains(ur.Role.Name))
            .Select(ur => ur.TenantId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        // A tenant-scoped token (tenant_id claim present) only carries authority in that tenant.
        var tenantClaim = user.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var claimedTenantId))
                return AuditAccessResult.Forbid("Audit data is only available to administrators.");

            administeredTenants = administeredTenants.Where(t => t == claimedTenantId).ToList();
        }

        if (administeredTenants.Count == 0)
            return AuditAccessResult.Forbid("Audit data is only available to administrators.");

        if (requestedTenantId.HasValue)
        {
            return administeredTenants.Contains(requestedTenantId.Value)
                ? AuditAccessResult.Allow(requestedTenantId.Value)
                : AuditAccessResult.Forbid("You can only access audit data of a tenant you administer.");
        }

        return administeredTenants.Count == 1
            ? AuditAccessResult.Allow(administeredTenants[0])
            : AuditAccessResult.NeedTenant("tenantId is required because you administer more than one tenant.");
    }
}
