using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

/// <summary>
/// UNUSED TABLE (task 5568): sign-in never writes this table. A "session" is a refresh token
/// (<see cref="RefreshToken"/>); the sessions list and every revoke route work on those. This class stays only as the
/// shape the sessions list returns (Id = refresh token id) and for rows left in the table; do not add writers.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// SHA256 hash of the session token for secure lookup
    /// </summary>
    [MaxLength(64)]
    public string SessionToken { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? IpAddress { get; set; }

    [MaxLength(500)]
    public string? UserAgent { get; set; }

    /// <summary>
    /// Parsed from UserAgent: e.g. "Chrome on Windows"
    /// </summary>
    [MaxLength(100)]
    public string? DeviceInfo { get; set; }

    /// <summary>
    /// Derived from IP (optional, for display purposes)
    /// </summary>
    [MaxLength(100)]
    public string? Location { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Reason for revocation: user_action, admin_action, expired, security
    /// </summary>
    [MaxLength(50)]
    public string? RevokedReason { get; set; }

    /// <summary>
    /// Flag indicating this is the caller's current session (set at query time, not persisted)
    /// </summary>
    public bool IsCurrent { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsActive => !IsRevoked && !IsExpired;
}
