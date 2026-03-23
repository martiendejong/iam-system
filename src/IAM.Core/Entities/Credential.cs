namespace IAM.Core.Entities;

/// <summary>
/// WebAuthn credential (passkey) associated with a user
/// </summary>
public class Credential
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// Credential ID (from authenticator)
    /// </summary>
    public required byte[] CredentialId { get; set; }

    /// <summary>
    /// Public key (from authenticator)
    /// </summary>
    public required byte[] PublicKey { get; set; }

    /// <summary>
    /// Counter to prevent replay attacks
    /// </summary>
    public uint SignCounter { get; set; }

    /// <summary>
    /// Credential type (e.g., "public-key")
    /// </summary>
    public required string CredType { get; set; }

    /// <summary>
    /// AAGUID (Authenticator Attestation GUID) - identifies the authenticator model
    /// </summary>
    public required Guid AaGuid { get; set; }

    /// <summary>
    /// Friendly name for this credential (e.g., "iPhone 15 Pro", "YubiKey 5")
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// User agent when credential was registered
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Device type hint (e.g., "phone", "computer", "security-key")
    /// </summary>
    public string? DeviceType { get; set; }

    /// <summary>
    /// Whether this credential can be used across devices (platform authenticator vs roaming)
    /// </summary>
    public bool IsBackupEligible { get; set; }

    /// <summary>
    /// Whether this credential has been backed up
    /// </summary>
    public bool IsBackedUp { get; set; }

    /// <summary>
    /// Transports supported by this authenticator (usb, nfc, ble, internal)
    /// </summary>
    public string[]? Transports { get; set; }

    /// <summary>
    /// When this credential was registered
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last time this credential was used
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Attestation format used during registration
    /// </summary>
    public string? AttestationFormat { get; set; }

    /// <summary>
    /// Attestation certificate chain (optional, for device verification)
    /// </summary>
    public byte[]? AttestationCertificate { get; set; }

    // Navigation property
    public User? User { get; set; }
}
