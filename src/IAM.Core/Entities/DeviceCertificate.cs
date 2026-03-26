namespace IAM.Core.Entities;

/// <summary>
/// Represents an X.509 certificate issued to an IoT device.
/// Supports full certificate lifecycle: issuance, rotation, revocation.
/// </summary>
public class DeviceCertificate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Device this certificate belongs to
    /// </summary>
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    /// <summary>
    /// Certificate serial number (unique identifier from CA)
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// Certificate thumbprint (SHA-256 hash of DER-encoded certificate)
    /// </summary>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Subject name from the certificate (typically the device ID)
    /// </summary>
    public string SubjectName { get; set; } = string.Empty;

    /// <summary>
    /// Issuer name (Intermediate CA that signed this certificate)
    /// </summary>
    public string IssuerName { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded public certificate (no private key)
    /// </summary>
    public string CertificatePem { get; set; } = string.Empty;

    /// <summary>
    /// Certificate validity start
    /// </summary>
    public DateTime NotBefore { get; set; }

    /// <summary>
    /// Certificate expiration date
    /// </summary>
    public DateTime NotAfter { get; set; }

    /// <summary>
    /// Certificate status: Active, Expired, Revoked, Pending
    /// </summary>
    public string Status { get; set; } = "Active";

    /// <summary>
    /// Reason for revocation (if revoked)
    /// </summary>
    public string? RevocationReason { get; set; }

    /// <summary>
    /// When the certificate was revoked
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// User who revoked the certificate
    /// </summary>
    public Guid? RevokedByUserId { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
