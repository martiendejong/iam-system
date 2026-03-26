namespace IAM.Core.Entities;

/// <summary>
/// Per-tenant branding configuration for custom login pages and white-label support.
/// One-to-one relationship with Tenant.
/// </summary>
public class TenantBranding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant this branding belongs to (one-to-one)
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// URL to the tenant's logo image
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Primary brand color (hex, e.g. "#4F46E5")
    /// </summary>
    public string? PrimaryColor { get; set; }

    /// <summary>
    /// Secondary brand color (hex, e.g. "#7C3AED")
    /// </summary>
    public string? SecondaryColor { get; set; }

    /// <summary>
    /// URL to the login page background image
    /// </summary>
    public string? BackgroundUrl { get; set; }

    /// <summary>
    /// Custom CSS injected into the login page
    /// </summary>
    public string? CustomCss { get; set; }

    /// <summary>
    /// HTML header block for branded emails
    /// </summary>
    public string? EmailHeaderHtml { get; set; }

    /// <summary>
    /// HTML footer block for branded emails
    /// </summary>
    public string? EmailFooterHtml { get; set; }

    /// <summary>
    /// URL to the tenant's favicon
    /// </summary>
    public string? FaviconUrl { get; set; }

    /// <summary>
    /// Custom title shown on the login page
    /// </summary>
    public string? LoginTitle { get; set; }

    /// <summary>
    /// Custom subtitle shown on the login page
    /// </summary>
    public string? LoginSubtitle { get; set; }

    /// <summary>
    /// Whether to hide the platform branding entirely (white-label mode)
    /// </summary>
    public bool WhiteLabelEnabled { get; set; }

    /// <summary>
    /// Custom domain for tenant-specific login (e.g. "login.acme.com")
    /// </summary>
    public string? CustomDomain { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
