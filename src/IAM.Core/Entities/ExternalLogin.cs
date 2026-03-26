namespace IAM.Core.Entities;

/// <summary>
/// Links an external identity provider account to an IAM user.
/// </summary>
public class ExternalLogin
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The IAM user this external login is linked to
    /// </summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Provider name (e.g., "Google", "Microsoft", "GitHub", "Apple")
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The user's unique ID from the external provider
    /// </summary>
    public string ProviderUserId { get; set; } = string.Empty;

    /// <summary>
    /// Email from the external provider (may differ from IAM user email)
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Display name from the external provider
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// When this external account was linked
    /// </summary>
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the user last authenticated via this provider
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Whether this is the user's primary external identity (used for display name/avatar)
    /// </summary>
    public bool IsPrimary { get; set; }
}
