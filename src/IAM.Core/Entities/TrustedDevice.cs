namespace IAM.Core.Entities;

/// <summary>
/// Represents a trusted device for a user, reducing risk score on subsequent logins.
/// Device fingerprint is a hash of browser/device characteristics.
/// </summary>
public class TrustedDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// SHA256 hash of device characteristics (screen resolution, timezone, plugins, etc.)
    /// </summary>
    public string DeviceFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable device name (e.g. "Chrome on Windows - Home PC")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Trust score from 0-100. Increases with successful logins, decreases with suspicious activity.
    /// </summary>
    public int TrustScore { get; set; } = 50;

    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Device trust expires after this date and must be re-verified.
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(90);

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsActive => !IsExpired;
}
