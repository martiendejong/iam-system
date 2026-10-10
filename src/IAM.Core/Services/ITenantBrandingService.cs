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
    /// Get branding for a tenant by its configured custom domain (used to resolve
    /// branding for requests arriving on a tenant's own domain, e.g. login.acme.com).
    /// </summary>
    Task<TenantBranding?> GetByCustomDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="GetByCustomDomainAsync"/>, but only when the tenant has proven it owns the domain (DNS TXT
    /// record, task 5150) and the domain is not a platform host. This is what the by-domain endpoint serves.
    /// </summary>
    Task<TenantBranding?> GetVerifiedByCustomDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>True for the platform's own hosts (configured Branding:PlatformHosts, the issuer host, localhost).</summary>
    bool IsPlatformHost(string? host);

    /// <summary>The TXT record a tenant must publish to prove it owns <paramref name="domain"/>.</summary>
    (string RecordName, string RecordValue) GetDomainVerificationRecord(Guid tenantId, string domain);

    Task<bool> IsDomainVerifiedAsync(Guid tenantId, string domain, CancellationToken ct = default);

    /// <summary>
    /// Create or update branding for a tenant. Upserts based on TenantId. Every value is validated/sanitized first
    /// (task 5150) and a <see cref="BrandingValidationException"/> is thrown for unacceptable input.
    /// </summary>
    /// <param name="requestHost">The host the request arrived on; it can never be claimed as a custom domain either.</param>
    Task<TenantBranding> UpsertAsync(Guid tenantId, TenantBranding branding, CancellationToken ct = default, string? requestHost = null);

    /// <summary>
    /// Delete branding for a tenant (resets to platform defaults).
    /// </summary>
    Task<bool> DeleteAsync(Guid tenantId, CancellationToken ct = default);
}
