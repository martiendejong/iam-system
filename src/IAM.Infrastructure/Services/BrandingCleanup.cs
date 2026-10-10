using IAM.Core.Security;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Startup pass (task 5150): re-validates every stored branding row. Custom CSS that no longer passes the allow-list is
/// removed, e-mail HTML is sanitized, titles are reduced to plain text, unsafe colors/URLs are cleared and a custom
/// domain that is not a plain host name is cleared. A valid custom domain is left in place: it is only served once its
/// DNS TXT proof is published (so every domain stored before this fix is unverified until the tenant publishes it).
/// Idempotent; rows that are already clean are not written.
/// </summary>
public static class BrandingCleanup
{
    public static async Task<int> RunAsync(IAMDbContext context, ILogger logger, CancellationToken ct = default)
    {
        var changed = 0;
        foreach (var branding in await context.TenantBrandings.ToListAsync(ct))
        {
            var dirty = false;

            dirty |= Set(branding.CustomCss, BrandingSanitizer.SafeCssOrNull(branding.CustomCss), v => branding.CustomCss = v);

            dirty |= Set(branding.EmailHeaderHtml, BrandingSanitizer.SanitizeEmailHtml(branding.EmailHeaderHtml), v => branding.EmailHeaderHtml = v);
            dirty |= Set(branding.EmailFooterHtml, BrandingSanitizer.SanitizeEmailHtml(branding.EmailFooterHtml), v => branding.EmailFooterHtml = v);
            dirty |= Set(branding.LoginTitle, BrandingSanitizer.StripToPlainText(branding.LoginTitle, BrandingSanitizer.MaxTitleLength), v => branding.LoginTitle = v);
            dirty |= Set(branding.LoginSubtitle, BrandingSanitizer.StripToPlainText(branding.LoginSubtitle, BrandingSanitizer.MaxSubtitleLength), v => branding.LoginSubtitle = v);
            dirty |= Set(branding.LogoUrl, BrandingSanitizer.SafeImageUrlOrNull(branding.LogoUrl), v => branding.LogoUrl = v);
            dirty |= Set(branding.BackgroundUrl, BrandingSanitizer.SafeImageUrlOrNull(branding.BackgroundUrl), v => branding.BackgroundUrl = v);
            dirty |= Set(branding.FaviconUrl, BrandingSanitizer.SafeImageUrlOrNull(branding.FaviconUrl), v => branding.FaviconUrl = v);

            if (BrandingSanitizer.ValidateColor(branding.PrimaryColor, "c") != null)
            {
                branding.PrimaryColor = null;
                dirty = true;
            }

            if (BrandingSanitizer.ValidateColor(branding.SecondaryColor, "c") != null)
            {
                branding.SecondaryColor = null;
                dirty = true;
            }

            if (!string.IsNullOrWhiteSpace(branding.CustomDomain))
            {
                var domain = BrandingSanitizer.NormalizeDomain(branding.CustomDomain);
                if (!string.Equals(domain, branding.CustomDomain, StringComparison.Ordinal))
                {
                    branding.CustomDomain = domain;
                    dirty = true;
                }
            }

            if (dirty)
            {
                branding.UpdatedAt = DateTime.UtcNow;
                changed++;
            }
        }

        if (changed > 0)
        {
            await context.SaveChangesAsync(ct);
            logger.LogWarning("Branding clean-up changed {Count} stored branding row(s) that did not pass validation", changed);
        }

        return changed;
    }

    private static bool Set(string? current, string? cleaned, Action<string?> assign)
    {
        var normalizedCurrent = string.IsNullOrEmpty(current) ? null : current;
        if (string.Equals(normalizedCurrent, cleaned, StringComparison.Ordinal))
            return false;

        assign(cleaned);
        return true;
    }
}
