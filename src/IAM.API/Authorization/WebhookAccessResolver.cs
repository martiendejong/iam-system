using System.Security.Claims;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Authorization;

/// <summary>
/// What a caller may manage in the webhook API (task 4702): SuperAdmin manages every tenant's
/// subscriptions; a tenant admin manages the tenants they administer; everyone else nothing.
/// </summary>
public sealed record WebhookAccess(bool IsPlatformAdmin, IReadOnlyList<Guid> AdministeredTenants)
{
    /// <summary>True when the caller can manage subscriptions of at least one tenant.</summary>
    public bool HasAny => IsPlatformAdmin || AdministeredTenants.Count > 0;

    public bool CanManage(Guid tenantId) => IsPlatformAdmin || AdministeredTenants.Contains(tenantId);

    public static WebhookAccess None { get; } = new(false, Array.Empty<Guid>());
}

public interface IWebhookAccessResolver
{
    Task<WebhookAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken ct);
}

public class WebhookAccessResolver : IWebhookAccessResolver
{
    private readonly IAMDbContext _context;

    public WebhookAccessResolver(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<WebhookAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        // Device and service-account tokens never manage webhooks.
        if (user.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null)
            return WebhookAccess.None;

        if (user.IsInRole("SuperAdmin"))
            return new WebhookAccess(true, Array.Empty<Guid>());

        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return WebhookAccess.None;

        // Role claims are global names and password-login tokens carry no tenant_id, so the tenants a caller
        // administers come from their UserRoles rows (null-tenant rows do not count) - the same tenant-admin
        // role set the audit endpoints use (task 4714).
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
                return WebhookAccess.None;
            tenants = tenants.Where(t => t == claimed).ToList();
        }

        return new WebhookAccess(false, tenants);
    }
}
