using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/branding")]
[Authorize]
public class TenantBrandingController : ControllerBase
{
    private readonly ITenantBrandingService _brandingService;
    private readonly IAMDbContext _context;
    private readonly ILogger<TenantBrandingController> _logger;

    public TenantBrandingController(ITenantBrandingService brandingService, IAMDbContext context, ILogger<TenantBrandingController> logger)
    {
        _brandingService = brandingService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when the caller may access the given tenant's branding. SuperAdmin can
    /// access any tenant. Otherwise mirrors GroupService.CreateGroupAsync's tenant-membership
    /// check: a tenant_id claim, when present, must match the target tenant, and the caller
    /// additionally needs an active UserRoles row for that tenant - password-login tokens carry
    /// no tenant_id claim at all, so a claim-only check would wrongly 403 a tenant admin's own
    /// tenant (task 4522 review).
    /// </summary>
    private async Task<bool> IsAuthorizedForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        if (User.IsInRole("SuperAdmin")) return true;

        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId)) return false;

        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var claimedTenantId) || claimedTenantId != tenantId)
                return false;
        }

        var now = DateTime.UtcNow;
        return await _context.UserRoles.AnyAsync(ur =>
            ur.UserId == userId
            && ur.TenantId == tenantId
            && (ur.ExpiresAt == null || ur.ExpiresAt > now), ct);
    }

    /// <summary>
    /// Get branding for a tenant (scoped to the caller's own tenant, or SuperAdmin).
    /// </summary>
    [HttpGet("{tenantId}")]
    public async Task<IActionResult> GetBranding(Guid tenantId, CancellationToken ct)
    {
        if (!await IsAuthorizedForTenantAsync(tenantId, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Access to this tenant's branding is not allowed." });

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
        var branding = await _brandingService.GetByTenantSlugAsync(tenantSlug, ct);

        if (branding == null)
        {
            return NotFound(new { error = "Tenant not found or no branding configured" });
        }

        // Return only the fields needed for login page rendering (no email templates)
        return Ok(new
        {
            tenantSlug,
            tenantName = branding.Tenant?.Name,
            logoUrl = branding.LogoUrl,
            primaryColor = branding.PrimaryColor ?? "#4F46E5",
            secondaryColor = branding.SecondaryColor ?? "#7C3AED",
            backgroundUrl = branding.BackgroundUrl,
            customCss = branding.CustomCss,
            faviconUrl = branding.FaviconUrl,
            loginTitle = branding.LoginTitle,
            loginSubtitle = branding.LoginSubtitle,
            whiteLabelEnabled = branding.WhiteLabelEnabled
        });
    }

    /// <summary>
    /// Get branding by custom domain (public endpoint for login pages served on a
    /// tenant's own domain, e.g. login.acme.com). No authentication required.
    /// </summary>
    [HttpGet("by-domain/{domain}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBrandingByDomain(string domain, CancellationToken ct)
    {
        var branding = await _brandingService.GetByCustomDomainAsync(domain, ct);

        if (branding == null)
        {
            return NotFound(new { error = "No tenant is configured for this domain" });
        }

        return Ok(new
        {
            tenantSlug = branding.Tenant?.Slug,
            tenantName = branding.Tenant?.Name,
            logoUrl = branding.LogoUrl,
            primaryColor = branding.PrimaryColor ?? "#4F46E5",
            secondaryColor = branding.SecondaryColor ?? "#7C3AED",
            backgroundUrl = branding.BackgroundUrl,
            customCss = branding.CustomCss,
            faviconUrl = branding.FaviconUrl,
            loginTitle = branding.LoginTitle,
            loginSubtitle = branding.LoginSubtitle,
            whiteLabelEnabled = branding.WhiteLabelEnabled
        });
    }

    /// <summary>
    /// Create or update branding for a tenant (scoped to the caller's own tenant, or SuperAdmin).
    /// </summary>
    [HttpPut("{tenantId}")]
    public async Task<IActionResult> UpsertBranding(Guid tenantId, [FromBody] UpsertBrandingRequest request, CancellationToken ct)
    {
        if (!await IsAuthorizedForTenantAsync(tenantId, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Access to this tenant's branding is not allowed." });

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

        var result = await _brandingService.UpsertAsync(tenantId, branding, ct);
        return Ok(MapToResponse(result));
    }

    /// <summary>
    /// Delete branding for a tenant (scoped to the caller's own tenant, or SuperAdmin).
    /// </summary>
    [HttpDelete("{tenantId}")]
    public async Task<IActionResult> DeleteBranding(Guid tenantId, CancellationToken ct)
    {
        if (!await IsAuthorizedForTenantAsync(tenantId, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Access to this tenant's branding is not allowed." });

        var deleted = await _brandingService.DeleteAsync(tenantId, ct);

        if (!deleted)
            return NotFound(new { error = "No branding found for this tenant" });

        return Ok(new { message = "Branding deleted successfully" });
    }

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
