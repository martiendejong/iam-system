using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

/// <summary>
/// Per-tenant SCIM bearer token for authenticating incoming SCIM provisioning requests.
/// Each tenant can have multiple tokens (e.g., for key rotation).
/// </summary>
public class ScimToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Display name for the token (e.g., "Okta Production", "Azure AD Staging")
    /// </summary>
    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// SHA256 hash of the bearer token value.
    /// The plaintext token is only shown once at creation time.
    /// </summary>
    [Required, MaxLength(128)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// First 8 characters of the token for identification in logs.
    /// </summary>
    [MaxLength(16)]
    public string TokenPrefix { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? Description { get; set; }
}
