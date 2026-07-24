using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class TenantBrandingService : ITenantBrandingService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<TenantBrandingService> _logger;

    public TenantBrandingService(IAMDbContext context, ILogger<TenantBrandingService> logger)
    {
        _context = context;
        _logger = logger;
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

    public async Task<TenantBranding> UpsertAsync(Guid tenantId, TenantBranding branding, CancellationToken ct = default)
    {
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
