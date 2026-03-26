namespace IAM.Core.Entities;

/// <summary>
/// Records user consent for OAuth2 client access to specific scopes.
/// Supports GDPR Article 7 (Conditions for consent) requirements.
/// </summary>
public class ConsentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The user who granted consent
    /// </summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// OAuth2 client ID that received consent
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated list of granted scopes (e.g., "openid,profile,email")
    /// </summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>
    /// When consent was granted
    /// </summary>
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When consent was revoked (null if still active)
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// IP address of the user when consent was granted
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent of the browser/client when consent was granted
    /// </summary>
    public string? UserAgent { get; set; }
}
