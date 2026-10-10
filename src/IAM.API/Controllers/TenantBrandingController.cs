using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Security;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/branding")]
[Authorize]
public class TenantBrandingController : ControllerBase
{
    private readonly ITenantBrandingService _brandingService;
    private readonly ILogger<TenantBrandingController> _logger;

    public TenantBrandingController(ITenantBrandingService brandingService, ILogger<TenantBrandingController> logger)
    {
        _brandingService = brandingService;
        _logger = logger;
    }

    /// <summary>
    /// Get branding for a tenant (admin, authenticated).
    /// </summary>
    [HttpGet("{tenantId}")]
    public async Task<IActionResult> GetBranding(Guid tenantId, CancellationToken ct)
    {
        var branding = await _brandingService.GetByTenantIdAsync(tenantId, ct);

        if (branding == null)
        {
            return Ok(new
            {
                tenantId,
                logoUrl = (string?)null,
                primaryColor = "#4F46E5",
                secondaryColor = "#7C3AED",
                backgroundUrl = (string?)null,
                customCss = (string?)null,
                emailHeaderHtml = (string?)null,
                emailFooterHtml = (string?)null,
                faviconUrl = (string?)null,
                loginTitle = (string?)null,
                loginSubtitle = (string?)null,
                whiteLabelEnabled = false,
                customDomain = (string?)null
            });
        }

        return Ok(MapToResponse(branding));
    }

    /// <summary>
    /// Get branding by tenant slug (public endpoint for login page rendering).
    /// No authentication required so login pages can be branded before the user logs in.
    /// </summary>
    [HttpGet("public/{tenantSlug}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublicBranding(string tenantSlug, CancellationToken ct)
    {
        // Task 5150: the platform's own login host always shows the default look, whatever ?tenant= says (a tenant
        // admin could otherwise send a victim to /auth/login?tenant=<slug> and restyle the platform's own page).
        // Elsewhere the slug only works on the tenant's own, ownership-verified custom domain.
        var requestHost = Request.Host.Host;
        if (string.IsNullOrEmpty(requestHost) || _brandingService.IsPlatformHost(requestHost))
            return NotFound(new { error = "Tenant not found or no branding configured" });

        var branding = await _brandingService.GetByTenantSlugAsync(tenantSlug, ct);
        var requestDomain = BrandingSanitizer.NormalizeDomain(requestHost);
        if (branding == null
            || requestDomain == null
            || !string.Equals(BrandingSanitizer.NormalizeDomain(branding.CustomDomain), requestDomain, StringComparison.Ordinal)
            || !await _brandingService.IsDomainVerifiedAsync(branding.TenantId, requestDomain, ct))
        {
            return NotFound(new { error = "Tenant not found or no branding configured" });
        }

        return Ok(PublicPayload(branding, tenantSlug));
    }

    /// <summary>
    /// Get branding by custom domain (public endpoint for login pages served on a
    /// tenant's own domain, e.g. login.acme.com). No authentication required.
    /// Only domains whose owner has published the DNS TXT proof are served (task 5150).
    /// </summary>
    [HttpGet("by-domain/{domain}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBrandingByDomain(string domain, CancellationToken ct)
    {
        var branding = await _brandingService.GetVerifiedByCustomDomainAsync(domain, ct);

        if (branding == null)
        {
            return NotFound(new { error = "No tenant is configured for this domain" });
        }

        return Ok(PublicPayload(branding, branding.Tenant?.Slug));
    }

    /// <summary>
    /// The DNS TXT record the tenant must publish to prove it owns its custom domain, and whether it is published now.
    /// </summary>
    [HttpGet("{tenantId}/domain-verification")]
    public async Task<IActionResult> GetDomainVerification(Guid tenantId, CancellationToken ct)
    {
        var branding = await _brandingService.GetByTenantIdAsync(tenantId, ct);
        var domain = BrandingSanitizer.NormalizeDomain(branding?.CustomDomain);
        if (domain == null)
            return NotFound(new { error = "No custom domain is configured for this tenant" });

        var (recordName, recordValue) = _brandingService.GetDomainVerificationRecord(tenantId, domain);
        return Ok(new
        {
            customDomain = domain,
            recordType = "TXT",
            recordName,
            recordValue,
            verified = await _brandingService.IsDomainVerifiedAsync(tenantId, domain, ct)
        });
    }

    /// <summary>
    /// Create or update branding for a tenant. CSS, e-mail HTML, titles, colors, URLs and the custom domain are
    /// validated or sanitized first (task 5150); unacceptable input is a 400 naming what to fix.
    /// </summary>
    [HttpPut("{tenantId}")]
    public async Task<IActionResult> UpsertBranding(Guid tenantId, [FromBody] UpsertBrandingRequest request, CancellationToken ct)
    {
        var branding = new TenantBranding
        {
            LogoUrl = request.LogoUrl,
            PrimaryColor = request.PrimaryColor,
            SecondaryColor = request.SecondaryColor,
            BackgroundUrl = request.BackgroundUrl,
            CustomCss = request.CustomCss,
            EmailHeaderHtml = request.EmailHeaderHtml,
            EmailFooterHtml = request.EmailFooterHtml,
            FaviconUrl = request.FaviconUrl,
            LoginTitle = request.LoginTitle,
            LoginSubtitle = request.LoginSubtitle,
            WhiteLabelEnabled = request.WhiteLabelEnabled,
            CustomDomain = request.CustomDomain
        };

        try
        {
            var result = await _brandingService.UpsertAsync(tenantId, branding, ct, Request.Host.Host);
            return Ok(MapToResponse(result));
        }
        catch (BrandingValidationException ex)
        {
            return BadRequest(new { error = ex.Message, errors = ex.Errors });
        }
    }

    /// <summary>
    /// Delete branding for a tenant (resets to platform defaults).
    /// </summary>
    [HttpDelete("{tenantId}")]
    public async Task<IActionResult> DeleteBranding(Guid tenantId, CancellationToken ct)
    {
        var deleted = await _brandingService.DeleteAsync(tenantId, ct);

        if (!deleted)
            return NotFound(new { error = "No branding found for this tenant" });

        return Ok(new { message = "Branding deleted successfully" });
    }

    /// <summary>The login-page fields only (no e-mail templates), re-checked on read so rows stored before task 5150 are never served raw.</summary>
    private static object PublicPayload(TenantBranding b, string? tenantSlug) => new
    {
        tenantSlug,
        tenantName = b.Tenant?.Name,
        logoUrl = BrandingSanitizer.SafeImageUrlOrNull(b.LogoUrl),
        primaryColor = BrandingSanitizer.ValidateColor(b.PrimaryColor, "c") == null ? b.PrimaryColor ?? "#4F46E5" : "#4F46E5",
        secondaryColor = BrandingSanitizer.ValidateColor(b.SecondaryColor, "c") == null ? b.SecondaryColor ?? "#7C3AED" : "#7C3AED",
        backgroundUrl = BrandingSanitizer.SafeImageUrlOrNull(b.BackgroundUrl),
        customCss = BrandingSanitizer.SafeCssOrNull(b.CustomCss),
        faviconUrl = BrandingSanitizer.SafeImageUrlOrNull(b.FaviconUrl),
        loginTitle = BrandingSanitizer.StripToPlainText(b.LoginTitle, BrandingSanitizer.MaxTitleLength),
        loginSubtitle = BrandingSanitizer.StripToPlainText(b.LoginSubtitle, BrandingSanitizer.MaxSubtitleLength),
        whiteLabelEnabled = b.WhiteLabelEnabled
    };

    private static object MapToResponse(TenantBranding b) => new
    {
        id = b.Id,
        tenantId = b.TenantId,
        logoUrl = b.LogoUrl,
        primaryColor = b.PrimaryColor,
        secondaryColor = b.SecondaryColor,
        backgroundUrl = b.BackgroundUrl,
        customCss = b.CustomCss,
        emailHeaderHtml = b.EmailHeaderHtml,
        emailFooterHtml = b.EmailFooterHtml,
        faviconUrl = b.FaviconUrl,
        loginTitle = b.LoginTitle,
        loginSubtitle = b.LoginSubtitle,
        whiteLabelEnabled = b.WhiteLabelEnabled,
        customDomain = b.CustomDomain,
        createdAt = b.CreatedAt,
        updatedAt = b.UpdatedAt
    };
}

public record UpsertBrandingRequest(
    string? LogoUrl,
    string? PrimaryColor,
    string? SecondaryColor,
    string? BackgroundUrl,
    string? CustomCss,
    string? EmailHeaderHtml,
    string? EmailFooterHtml,
    string? FaviconUrl,
    string? LoginTitle,
    string? LoginSubtitle,
    bool WhiteLabelEnabled = false,
    string? CustomDomain = null
);
