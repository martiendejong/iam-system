using System.Security.Claims;
using Hazina.Security.ApiKeys;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Authorization;

/// <summary>
/// What a caller may manage in the secrets vault API (task 4704): SuperAdmin manages every secret (including the
/// global ones without a tenant); a tenant administrator manages only secrets of the tenants they administer;
/// everyone else nothing.
/// </summary>
public sealed record SecretsAccess(bool IsPlatformAdmin, IReadOnlyList<Guid> AdministeredTenants)
{
    /// <summary>True when the caller can manage secrets of at least one tenant (or all of them).</summary>
    public bool HasAny => IsPlatformAdmin || AdministeredTenants.Count > 0;

    /// <summary>A secret without a tenant (global) is SuperAdmin only.</summary>
    public bool CanManage(Guid? tenantId) =>
        IsPlatformAdmin || (tenantId.HasValue && AdministeredTenants.Contains(tenantId.Value));

    public static SecretsAccess None { get; } = new(false, Array.Empty<Guid>());
}

public interface ISecretsAccessResolver
{
    Task<SecretsAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken ct);
}

public class SecretsAccessResolver : ISecretsAccessResolver
{
    private readonly IAMDbContext _context;

    public SecretsAccessResolver(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<SecretsAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        // API keys first: a key issued by a user carries that user's id, but it is not that user's roles.
        // Device and service-account tokens never manage secrets either.
        if (user.IsApiKey() || user.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null)
            return SecretsAccess.None;

        if (user.IsInRole("SuperAdmin"))
            return new SecretsAccess(true, Array.Empty<Guid>());

        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return SecretsAccess.None;

        // Role claims are global names and password-login tokens carry no tenant_id, so the tenants a caller
        // administers come from their UserRoles rows (null-tenant rows do not count) - same tenant-admin role set
        // as the audit and webhook endpoints (tasks 4714, 4702).
        var now = DateTime.UtcNow;
        var tenants = await _context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId
                && ur.TenantId != null
                && (ur.ExpiresAt == null || ur.ExpiresAt > now)
                && AuditAccessResolver.TenantAdminRoles.Contains(ur.Role.Name))
            .Select(ur => ur.TenantId!.Value)
            .Distinct()
            .ToListAsync(ct);

        // A tenant-scoped token (tenant_id claim present) only carries authority in that tenant.
        var tenantClaim = user.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var claimed))
                return SecretsAccess.None;
            tenants = tenants.Where(t => t == claimed).ToList();
        }

        return new SecretsAccess(false, tenants);
    }
}
