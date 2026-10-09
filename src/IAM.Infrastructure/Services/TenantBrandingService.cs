using IAM.Core.Entities;
using IAM.Core.Security;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class TenantBrandingService : ITenantBrandingService
{
    private readonly IAMDbContext _context;
    private readonly IDomainOwnershipVerifier _domainVerifier;
    private readonly HashSet<string> _platformHosts;
    private readonly ILogger<TenantBrandingService> _logger;

    public TenantBrandingService(
        IAMDbContext context,
        IDomainOwnershipVerifier domainVerifier,
        IConfiguration configuration,
        ILogger<TenantBrandingService> logger)
    {
        _context = context;
        _domainVerifier = domainVerifier;
        _logger = logger;

        _platformHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };
        foreach (var host in configuration.GetSection("Branding:PlatformHosts").Get<string[]>() ?? Array.Empty<string>())
            _platformHosts.Add(host.Trim());
        foreach (var key in new[] { "Jwt:Issuer", "Issuer" })
        {
            if (Uri.TryCreate(configuration[key], UriKind.Absolute, out var issuer))
                _platformHosts.Add(issuer.Host);
        }
    }

    public bool IsPlatformHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && _platformHosts.Contains(host.Trim().TrimEnd('.'));

    public (string RecordName, string RecordValue) GetDomainVerificationRecord(Guid tenantId, string domain) =>
        (_domainVerifier.RecordName(domain), _domainVerifier.RecordValue(tenantId, domain));

    public Task<bool> IsDomainVerifiedAsync(Guid tenantId, string domain, CancellationToken ct = default) =>
        _domainVerifier.IsVerifiedAsync(tenantId, domain, ct);

    public async Task<TenantBranding?> GetVerifiedByCustomDomainAsync(string domain, CancellationToken ct = default)
    {
        var normalized = BrandingSanitizer.NormalizeDomain(domain);
        if (normalized == null || IsPlatformHost(normalized))
            return null;

        var branding = await GetByCustomDomainAsync(normalized, ct);
        if (branding == null || !await _domainVerifier.IsVerifiedAsync(branding.TenantId, normalized, ct))
            return null;

        return branding;
    }

    public async Task<TenantBranding?> GetByTenantIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.TenantBrandings
            .FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);
    }

    public async Task<TenantBranding?> GetByTenantSlugAsync(string slug, CancellationToken ct = default)
    {
        return await _context.TenantBrandings
            .Include(b => b.Tenant)
            .FirstOrDefaultAsync(b => b.Tenant.Slug == slug, ct);
    }

    public async Task<TenantBranding?> GetByCustomDomainAsync(string domain, CancellationToken ct = default)
    {
        return await _context.TenantBrandings
            .Include(b => b.Tenant)
            .FirstOrDefaultAsync(b => b.CustomDomain == domain, ct);
    }

    public async Task<TenantBranding> UpsertAsync(
        Guid tenantId, TenantBranding branding, CancellationToken ct = default, string? requestHost = null)
    {
        await ValidateAndNormalizeAsync(tenantId, branding, requestHost, ct);

        var existing = await _context.TenantBrandings
            .FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);

        if (existing == null)
        {
            branding.TenantId = tenantId;
            branding.CreatedAt = DateTime.UtcNow;
            branding.UpdatedAt = DateTime.UtcNow;
            _context.TenantBrandings.Add(branding);

            _logger.LogInformation("Created branding for tenant {TenantId}", tenantId);
        }
        else
        {
            existing.LogoUrl = branding.LogoUrl;
            existing.PrimaryColor = branding.PrimaryColor;
            existing.SecondaryColor = branding.SecondaryColor;
            existing.BackgroundUrl = branding.BackgroundUrl;
            existing.CustomCss = branding.CustomCss;
            existing.EmailHeaderHtml = branding.EmailHeaderHtml;
            existing.EmailFooterHtml = branding.EmailFooterHtml;
            existing.FaviconUrl = branding.FaviconUrl;
            existing.LoginTitle = branding.LoginTitle;
            existing.LoginSubtitle = branding.LoginSubtitle;
            existing.WhiteLabelEnabled = branding.WhiteLabelEnabled;
            existing.CustomDomain = branding.CustomDomain;
            existing.UpdatedAt = DateTime.UtcNow;

            branding = existing;
            _logger.LogInformation("Updated branding for tenant {TenantId}", tenantId);
        }

        // Audit log
        _context.AuditLogs.Add(new AuditLog
        {
            Action = existing == null ? "BrandingCreated" : "BrandingUpdated",
            Resource = "TenantBranding",
            Details = System.Text.Json.JsonSerializer.Serialize(new { tenantId })
        });

        await _context.SaveChangesAsync(ct);
        return branding;
    }

    /// <summary>Checks every field, replaces CSS / e-mail HTML / URLs with their normalized form and throws with all problems found.</summary>
    private async Task ValidateAndNormalizeAsync(Guid tenantId, TenantBranding branding, string? requestHost, CancellationToken ct)
    {
        var errors = new List<string>();
        void Add(string? error)
        {
            if (error != null)
                errors.Add(error);
        }

        Add(BrandingSanitizer.ValidateColor(branding.PrimaryColor, "Primary color"));
        Add(BrandingSanitizer.ValidateColor(branding.SecondaryColor, "Secondary color"));
        Add(BrandingSanitizer.ValidateImageUrl(branding.LogoUrl, "Logo URL"));
        Add(BrandingSanitizer.ValidateImageUrl(branding.BackgroundUrl, "Background URL"));
        Add(BrandingSanitizer.ValidateImageUrl(branding.FaviconUrl, "Favicon URL"));
        Add(BrandingSanitizer.ValidatePlainText(branding.LoginTitle, "Login title", BrandingSanitizer.MaxTitleLength));
        Add(BrandingSanitizer.ValidatePlainText(branding.LoginSubtitle, "Login subtitle", BrandingSanitizer.MaxSubtitleLength));

        var cssError = BrandingSanitizer.ValidateCss(branding.CustomCss, out var css);
        Add(cssError);
        branding.CustomCss = cssError == null && css.Length > 0 ? css : null;

        branding.EmailHeaderHtml = BrandingSanitizer.SanitizeEmailHtml(branding.EmailHeaderHtml);
        branding.EmailFooterHtml = BrandingSanitizer.SanitizeEmailHtml(branding.EmailFooterHtml);

        if (string.IsNullOrWhiteSpace(branding.CustomDomain))
        {
            branding.CustomDomain = null;
        }
        else
        {
            var domain = BrandingSanitizer.NormalizeDomain(branding.CustomDomain);
            if (domain == null)
            {
                errors.Add("Custom domain must be a plain host name such as login.example.com (no scheme, port, path or IP address).");
            }
            else if (IsPlatformHost(domain) || string.Equals(domain, requestHost?.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("The platform's own host name cannot be used as a custom domain.");
            }
            else if (await _context.TenantBrandings.AnyAsync(b => b.CustomDomain == domain && b.TenantId != tenantId, ct))
            {
                errors.Add("This custom domain is already claimed by another tenant.");
            }

            branding.CustomDomain = domain;
        }

        if (errors.Count > 0)
            throw new BrandingValidationException(errors);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, CancellationToken ct = default)
    {
        var existing = await _context.TenantBrandings
            .FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);

        if (existing == null)
            return false;

        _context.TenantBrandings.Remove(existing);

        _context.AuditLogs.Add(new AuditLog
        {
            Action = "BrandingDeleted",
            Resource = "TenantBranding",
            Details = System.Text.Json.JsonSerializer.Serialize(new { tenantId })
        });

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted branding for tenant {TenantId}", tenantId);

        return true;
    }
}
