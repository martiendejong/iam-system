using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing per-tenant branding configuration.
/// </summary>
public interface ITenantBrandingService
{
    /// <summary>
    /// Get branding for a tenant by tenant ID.
    /// </summary>
    Task<TenantBranding?> GetByTenantIdAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Get branding for a tenant by tenant slug (used by public login page endpoint).
    /// </summary>
    Task<TenantBranding?> GetByTenantSlugAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Create or update branding for a tenant. Upserts based on TenantId.
    /// </summary>
    Task<TenantBranding> UpsertAsync(Guid tenantId, TenantBranding branding, CancellationToken ct = default);

    /// <summary>
    /// Delete branding for a tenant (resets to platform defaults).
    /// </summary>
    Task<bool> DeleteAsync(Guid tenantId, CancellationToken ct = default);
}
