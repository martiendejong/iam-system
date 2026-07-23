using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

public class TokenConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The OAuth2/OpenIddict client application this configuration applies to.
    /// </summary>
    [Required, MaxLength(255)]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Optional tenant scope. When null, applies to all tenants for this client.
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Access token lifetime in minutes. Default: 15 minutes.
    /// </summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 15;

    /// <summary>
    /// Refresh token lifetime in days. Default: 7 days.
    /// </summary>
    public int RefreshTokenLifetimeDays { get; set; } = 7;

    /// <summary>
    /// Whether to include user roles in the token claims.
    /// </summary>
    public bool IncludeRoles { get; set; } = true;

    /// <summary>
    /// Whether to include role permissions in the token claims.
    /// </summary>
    public bool IncludePermissions { get; set; } = false;

    /// <summary>
    /// Whether to include group memberships in the token claims.
    /// </summary>
    public bool IncludeGroups { get; set; } = false;

    /// <summary>
    /// Custom namespace prefix for non-standard claims (e.g. "https://myapp.com/").
    /// </summary>
    [MaxLength(500)]
    public string? CustomNamespace { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
