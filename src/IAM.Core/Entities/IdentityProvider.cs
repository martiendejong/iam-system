namespace IAM.Core.Entities;

/// <summary>
/// Represents an external identity provider (OAuth2, SAML, OIDC) for social and enterprise SSO login.
/// </summary>
public class IdentityProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Internal name (e.g., "google", "azure-ad-contoso")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Display name shown to users (e.g., "Sign in with Google")
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Provider type (Google, Microsoft, GitHub, Apple, SAML, OIDC)
    /// </summary>
    public IdentityProviderType Type { get; set; }

    /// <summary>
    /// Tenant this provider belongs to. Null = global provider available to all tenants.
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// OAuth2/OIDC Client ID from the external provider
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2/OIDC Client Secret (stored encrypted)
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// SAML/OIDC metadata URL for automatic configuration discovery
    /// </summary>
    public string? MetadataUrl { get; set; }

    /// <summary>
    /// JSON mapping of external attributes to IAM user fields.
    /// Example: {"email": "mail", "firstName": "given_name", "lastName": "family_name"}
    /// </summary>
    public string? AttributeMapping { get; set; }

    /// <summary>
    /// Whether this identity provider is enabled
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Automatically create IAM users on first login via this provider
    /// </summary>
    public bool AutoCreateUsers { get; set; } = true;

    /// <summary>
    /// Default role to assign to auto-created users. Null = no default role.
    /// </summary>
    public Guid? DefaultRoleId { get; set; }
    public Role? DefaultRole { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum IdentityProviderType
{
    Google,
    Microsoft,
    GitHub,
    Apple,
    SAML,
    OIDC
}
