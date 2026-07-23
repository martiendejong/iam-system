using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

/// <summary>
/// Represents a token exchange record per RFC 8693 (OAuth 2.0 Token Exchange).
/// Tracks the mapping between original and exchanged tokens for audit and validation.
/// </summary>
public class TokenExchange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// SHA256 hash of the original (subject) token. Never store raw tokens.
    /// </summary>
    [Required, MaxLength(128)]
    public string OriginalTokenHash { get; set; } = string.Empty;

    /// <summary>
    /// SHA256 hash of the exchanged (output) token.
    /// </summary>
    [Required, MaxLength(128)]
    public string ExchangedTokenHash { get; set; } = string.Empty;

    /// <summary>
    /// The subject (user or service account) the token was issued for.
    /// </summary>
    public Guid SubjectId { get; set; }

    /// <summary>
    /// The target service/audience the exchanged token is scoped to.
    /// </summary>
    [Required, MaxLength(255)]
    public string TargetService { get; set; } = string.Empty;

    /// <summary>
    /// Scopes granted in the exchanged token.
    /// </summary>
    [MaxLength(1000)]
    public string? Scopes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether this exchange has been revoked.
    /// </summary>
    public bool IsRevoked { get; set; } = false;

    /// <summary>
    /// The service account that initiated the exchange, if applicable.
    /// </summary>
    public Guid? ServiceAccountId { get; set; }
    public ServiceAccount? ServiceAccount { get; set; }
}
