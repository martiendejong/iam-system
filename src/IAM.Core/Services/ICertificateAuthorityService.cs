using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface ICertificateAuthorityService
{
    Task<CaInfo> GetCaInfoAsync(CancellationToken ct = default);
    Task<(DeviceCertificate Certificate, string PrivateKeyPem)> IssueCertificateAsync(Guid deviceId, CertificateRequest request, Guid? issuedByUserId = null, CancellationToken ct = default);
    Task<bool> RevokeCertificateAsync(Guid certificateId, string reason, Guid? revokedByUserId = null, CancellationToken ct = default);
    Task<CertificateValidationResult> ValidateCertificateAsync(string certificatePem, CancellationToken ct = default);
    Task<List<DeviceCertificate>> GetExpiringCertificatesAsync(int daysBeforeExpiry = 30, CancellationToken ct = default);
    Task<(DeviceCertificate NewCertificate, string PrivateKeyPem)> RenewCertificateAsync(Guid certificateId, Guid? renewedByUserId = null, CancellationToken ct = default);
    Task<string> GetCrlPemAsync(CancellationToken ct = default); // Certificate Revocation List
}

public class CertificateRequest
{
    public string CommonName { get; set; } = string.Empty;
    public string? Organization { get; set; }
    public string? OrganizationalUnit { get; set; }
    public int ValidityDays { get; set; } = 365;
    public int KeySizeInBits { get; set; } = 2048;
}

public class CertificateValidationResult
{
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public bool IsExpired { get; set; }
    public bool IsRevoked { get; set; }
    public bool IsTrusted { get; set; } // Issued by our CA
    public DateTime? NotBefore { get; set; }
    public DateTime? NotAfter { get; set; }
    public string? SubjectName { get; set; }
    public string? SerialNumber { get; set; }
}

public class CaInfo
{
    public string CaCertificatePem { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public DateTime NotBefore { get; set; }
    public DateTime NotAfter { get; set; }
    public string Thumbprint { get; set; } = string.Empty;
    public int CertificatesIssued { get; set; }
    public int CertificatesRevoked { get; set; }
    public int CertificatesActive { get; set; }
}
