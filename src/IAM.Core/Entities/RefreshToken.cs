namespace IAM.Core.Entities;

using IAM.Core;

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Hashed refresh token (store hash, not plain text)
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// IP address where token was created
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent where token was created
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// True when the session this token belongs to was created (or last rotated) with
    /// "Remember me" checked. Carried forward onto each newly-issued token on rotation
    /// so <see cref="AuthConstants.RememberMeMinimumDays"/> keeps applying for as long as
    /// the user keeps refreshing, not just at the original login.
    /// </summary>
    public bool RememberMe { get; set; }

    /// <summary>
    /// Identifies the sign-in (device session) this token belongs to. Set to the first token's own id at
    /// login and carried unchanged through every rotation, so a replayed token can end just its own
    /// session chain instead of every session the user has. Null on tokens issued before this column existed.
    /// </summary>
    public Guid? FamilyId { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsActive => !IsRevoked && !IsExpired;
}
