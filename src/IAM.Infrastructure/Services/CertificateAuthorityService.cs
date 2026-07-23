using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using X509CertificateRequest = System.Security.Cryptography.X509Certificates.CertificateRequest;
using CertificateRequest = IAM.Core.Services.CertificateRequest;

namespace IAM.Infrastructure.Services;

public class CertificateAuthorityService : ICertificateAuthorityService
{
    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CertificateAuthorityService> _logger;

    private readonly string _caCertificatePath;
    private readonly string _caCertificatePassword;
    private readonly int _defaultValidityDays;
    private readonly int _rootValidityYears;
    private readonly string _caSubjectName;

    private static readonly SemaphoreSlim _caLock = new(1, 1);

    public CertificateAuthorityService(
        IAMDbContext context,
        IConfiguration configuration,
        ILogger<CertificateAuthorityService> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;

        _caCertificatePath = configuration["Ca:CertificatePath"] ?? "./ca-certs/ca.pfx";
        _caCertificatePassword = configuration["Ca:CertificatePassword"] ?? "IAM-CA-Default-Password";
        _defaultValidityDays = configuration.GetValue<int>("Ca:DefaultValidityDays", 365);
        _rootValidityYears = configuration.GetValue<int>("Ca:RootValidityYears", 10);
        _caSubjectName = configuration["Ca:SubjectName"] ?? "CN=IAM System CA, O=IAM System";
    }

    public async Task<CaInfo> GetCaInfoAsync(CancellationToken ct = default)
    {
        using var caCert = await GetOrCreateCaCertificateAsync(ct);

        var issued = await _context.DeviceCertificates.CountAsync(ct);
        var revoked = await _context.DeviceCertificates.CountAsync(c => c.Status == "Revoked", ct);
        var active = await _context.DeviceCertificates.CountAsync(c => c.Status == "Active", ct);

        return new CaInfo
        {
            CaCertificatePem = ExportCertificateToPem(caCert),
            SubjectName = caCert.Subject,
            NotBefore = caCert.NotBefore.ToUniversalTime(),
            NotAfter = caCert.NotAfter.ToUniversalTime(),
            Thumbprint = caCert.Thumbprint,
            CertificatesIssued = issued,
            CertificatesRevoked = revoked,
            CertificatesActive = active
        };
    }

    public async Task<(DeviceCertificate Certificate, string PrivateKeyPem)> IssueCertificateAsync(
        Guid deviceId,
        CertificateRequest request,
        Guid? issuedByUserId = null,
        CancellationToken ct = default)
    {
        // Verify device exists
        var device = await _context.Devices.FindAsync(new object[] { deviceId }, ct)
            ?? throw new InvalidOperationException($"Device {deviceId} not found");

        if (!device.IsActive)
            throw new InvalidOperationException($"Device {deviceId} is not active");

        var validityDays = request.ValidityDays > 0 ? request.ValidityDays : _defaultValidityDays;
        var keySizeBits = request.KeySizeInBits >= 2048 ? request.KeySizeInBits : 2048;

        using var caCert = await GetOrCreateCaCertificateAsync(ct);

        // Generate device key pair
        using var deviceKey = RSA.Create(keySizeBits);

        // Build the subject name
        var subjectBuilder = new StringBuilder($"CN={request.CommonName}");
        if (!string.IsNullOrWhiteSpace(request.Organization))
            subjectBuilder.Append($", O={request.Organization}");
        if (!string.IsNullOrWhiteSpace(request.OrganizationalUnit))
            subjectBuilder.Append($", OU={request.OrganizationalUnit}");

        var subjectName = new X500DistinguishedName(subjectBuilder.ToString());

        // Create certificate request (CSR)
        var csr = new X509CertificateRequest(
            subjectName,
            deviceKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Add extensions
        csr.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));

        csr.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));

        csr.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new("1.3.6.1.5.5.7.3.2") // Client Authentication
                },
                false));

        csr.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(csr.PublicKey, false));

        // Generate a unique serial number
        var serialBytes = new byte[16];
        RandomNumberGenerator.Fill(serialBytes);
        serialBytes[0] &= 0x7F; // Ensure positive serial number

        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore.AddDays(validityDays);

        // Sign with CA certificate
        using var caPrivateKey = caCert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("CA certificate does not contain a private key");

        var signedCert = csr.Create(
            caCert.SubjectName,
            X509SignatureGenerator.CreateForRSA(caPrivateKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            serialBytes);

        // Export the signed certificate PEM (public certificate only)
        var certPem = ExportCertificateToPem(signedCert);

        // Export the private key PEM
        var privateKeyPem = ExportPrivateKeyToPem(deviceKey);

        // Create DeviceCertificate entity
        var deviceCertificate = new DeviceCertificate
        {
            DeviceId = deviceId,
            SerialNumber = FormatSerialNumber(serialBytes),
            Thumbprint = signedCert.Thumbprint,
            SubjectName = signedCert.Subject,
            IssuerName = signedCert.Issuer,
            CertificatePem = certPem,
            NotBefore = notBefore.UtcDateTime,
            NotAfter = notAfter.UtcDateTime,
            Status = "Active",
            CreatedAt = DateTime.UtcNow
        };

        _context.DeviceCertificates.Add(deviceCertificate);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Issued certificate {SerialNumber} for device {DeviceId}, valid until {NotAfter}",
            deviceCertificate.SerialNumber, deviceId, notAfter);

        return (deviceCertificate, privateKeyPem);
    }

    public async Task<bool> RevokeCertificateAsync(
        Guid certificateId,
        string reason,
        Guid? revokedByUserId = null,
        CancellationToken ct = default)
    {
        var certificate = await _context.DeviceCertificates.FindAsync(new object[] { certificateId }, ct);
        if (certificate == null)
            return false;

        if (certificate.Status == "Revoked")
            return true; // Already revoked

        certificate.Status = "Revoked";
        certificate.RevocationReason = reason;
        certificate.RevokedAt = DateTime.UtcNow;
        certificate.RevokedByUserId = revokedByUserId;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Revoked certificate {CertificateId} (serial: {SerialNumber}), reason: {Reason}",
            certificateId, certificate.SerialNumber, reason);

        return true;
    }

    public async Task<CertificateValidationResult> ValidateCertificateAsync(
        string certificatePem,
        CancellationToken ct = default)
    {
        var result = new CertificateValidationResult();

        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadCertificate(
                Encoding.UTF8.GetBytes(certificatePem));
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Error = $"Failed to parse certificate: {ex.Message}";
            return result;
        }

        using (cert)
        {
            result.SubjectName = cert.Subject;
            result.SerialNumber = cert.SerialNumber;
            result.NotBefore = cert.NotBefore.ToUniversalTime();
            result.NotAfter = cert.NotAfter.ToUniversalTime();

            // Check expiry
            var now = DateTime.UtcNow;
            result.IsExpired = now > cert.NotAfter.ToUniversalTime() || now < cert.NotBefore.ToUniversalTime();

            // Check if issued by our CA
            using var caCert = await GetOrCreateCaCertificateAsync(ct);
            result.IsTrusted = ValidateCertificateChain(cert, caCert);

            // Check revocation status in database
            var dbCert = await _context.DeviceCertificates
                .FirstOrDefaultAsync(c => c.Thumbprint == cert.Thumbprint, ct);

            if (dbCert != null)
            {
                result.IsRevoked = dbCert.Status == "Revoked";
            }
            else
            {
                // Certificate not found in our database - it was not issued by us
                // or has been removed
                if (result.IsTrusted)
                {
                    result.IsRevoked = false; // Trusted by chain but not in DB
                }
            }

            result.IsValid = result.IsTrusted && !result.IsExpired && !result.IsRevoked;

            if (!result.IsValid && result.Error == null)
            {
                var errors = new List<string>();
                if (!result.IsTrusted) errors.Add("Certificate not issued by this CA");
                if (result.IsExpired) errors.Add("Certificate has expired");
                if (result.IsRevoked) errors.Add("Certificate has been revoked");
                result.Error = string.Join("; ", errors);
            }
        }

        return result;
    }

    public async Task<List<DeviceCertificate>> GetExpiringCertificatesAsync(
        int daysBeforeExpiry = 30,
        CancellationToken ct = default)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(daysBeforeExpiry);

        return await _context.DeviceCertificates
            .Include(c => c.Device)
            .Where(c => c.Status == "Active" && c.NotAfter <= cutoffDate)
            .OrderBy(c => c.NotAfter)
            .ToListAsync(ct);
    }

    public async Task<(DeviceCertificate NewCertificate, string PrivateKeyPem)> RenewCertificateAsync(
        Guid certificateId,
        Guid? renewedByUserId = null,
        CancellationToken ct = default)
    {
        var existingCert = await _context.DeviceCertificates
            .Include(c => c.Device)
            .FirstOrDefaultAsync(c => c.Id == certificateId, ct)
            ?? throw new InvalidOperationException($"Certificate {certificateId} not found");

        if (existingCert.Device == null)
            throw new InvalidOperationException($"Device for certificate {certificateId} not found");

        // Parse the existing certificate to extract subject details
        X509Certificate2 parsedCert;
        try
        {
            parsedCert = X509CertificateLoader.LoadCertificate(
                Encoding.UTF8.GetBytes(existingCert.CertificatePem));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse existing certificate: {ex.Message}", ex);
        }

        string commonName;
        string? organization = null;
        string? organizationalUnit = null;

        using (parsedCert)
        {
            commonName = GetRdnValue(parsedCert.Subject, "CN") ?? existingCert.Device.DeviceId;
            organization = GetRdnValue(parsedCert.Subject, "O");
            organizationalUnit = GetRdnValue(parsedCert.Subject, "OU");
        }

        // Calculate the same validity period as the original
        var originalValidityDays = (int)(existingCert.NotAfter - existingCert.NotBefore).TotalDays;
        var validityDays = originalValidityDays > 0 ? originalValidityDays : _defaultValidityDays;

        // Issue new certificate with the same subject
        var request = new CertificateRequest
        {
            CommonName = commonName,
            Organization = organization,
            OrganizationalUnit = organizationalUnit,
            ValidityDays = validityDays,
            KeySizeInBits = 2048
        };

        var (newCert, privateKeyPem) = await IssueCertificateAsync(
            existingCert.DeviceId, request, renewedByUserId, ct);

        // Revoke old certificate
        await RevokeCertificateAsync(
            certificateId, "Renewed - superseded by new certificate", renewedByUserId, ct);

        _logger.LogInformation(
            "Renewed certificate {OldCertId} -> {NewCertId} for device {DeviceId}",
            certificateId, newCert.Id, existingCert.DeviceId);

        return (newCert, privateKeyPem);
    }

    public async Task<string> GetCrlPemAsync(CancellationToken ct = default)
    {
        var revokedCerts = await _context.DeviceCertificates
            .Where(c => c.Status == "Revoked")
            .OrderBy(c => c.RevokedAt)
            .Select(c => new { c.SerialNumber, c.RevokedAt, c.RevocationReason })
            .ToListAsync(ct);

        using var caCert = await GetOrCreateCaCertificateAsync(ct);

        // Build CRL in a human-readable + machine-parseable PEM-like format
        // .NET does not have a built-in CRL builder, so we construct a structured text CRL
        // that includes all revoked serial numbers with timestamps.
        // For production mTLS validation, consider using Bouncy Castle for ASN.1 DER CRL encoding.
        var sb = new StringBuilder();
        sb.AppendLine("-----BEGIN X509 CRL-----");
        sb.AppendLine($"Issuer: {caCert.Subject}");
        sb.AppendLine($"ThisUpdate: {DateTime.UtcNow:O}");
        sb.AppendLine($"NextUpdate: {DateTime.UtcNow.AddHours(24):O}");
        sb.AppendLine($"RevokedCertificates: {revokedCerts.Count}");
        sb.AppendLine();

        foreach (var cert in revokedCerts)
        {
            sb.AppendLine($"Serial: {cert.SerialNumber}");
            sb.AppendLine($"RevokedAt: {cert.RevokedAt:O}");
            sb.AppendLine($"Reason: {cert.RevocationReason ?? "unspecified"}");
            sb.AppendLine();
        }

        sb.AppendLine("-----END X509 CRL-----");

        return sb.ToString();
    }

    // ─── Private helpers ──────────────────────────────────────────────

    private async Task<X509Certificate2> GetOrCreateCaCertificateAsync(CancellationToken ct)
    {
        // Fast path: check if PFX already exists on disk
        if (File.Exists(_caCertificatePath))
        {
            return new X509Certificate2(
                _caCertificatePath,
                _caCertificatePassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }

        // Slow path: generate new CA certificate (thread-safe)
        await _caLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (File.Exists(_caCertificatePath))
            {
                return new X509Certificate2(
                    _caCertificatePath,
                    _caCertificatePassword,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            }

            return GenerateAndStoreCaCertificate();
        }
        finally
        {
            _caLock.Release();
        }
    }

    private X509Certificate2 GenerateAndStoreCaCertificate()
    {
        _logger.LogInformation("Generating new CA root certificate: {Subject}", _caSubjectName);

        using var caKey = RSA.Create(4096);
        var subjectName = new X500DistinguishedName(_caSubjectName);

        var csr = new X509CertificateRequest(
            subjectName,
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // CA-specific extensions
        csr.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, true, 1, true));

        csr.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
                true));

        csr.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(csr.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore.AddYears(_rootValidityYears);

        var caCert = csr.CreateSelfSigned(notBefore, notAfter);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(Path.GetFullPath(_caCertificatePath));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Export to PFX and save
        var pfxBytes = caCert.Export(X509ContentType.Pfx, _caCertificatePassword);
        File.WriteAllBytes(_caCertificatePath, pfxBytes);

        _logger.LogInformation(
            "CA certificate generated and stored at {Path}, valid until {NotAfter}",
            _caCertificatePath, notAfter);

        // Return a new instance loaded from the PFX to ensure key availability
        return new X509Certificate2(
            pfxBytes,
            _caCertificatePassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    private static string ExportCertificateToPem(X509Certificate2 cert)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-----BEGIN CERTIFICATE-----");
        sb.AppendLine(Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks));
        sb.AppendLine("-----END CERTIFICATE-----");
        return sb.ToString();
    }

    private static string ExportPrivateKeyToPem(RSA key)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-----BEGIN RSA PRIVATE KEY-----");
        sb.AppendLine(Convert.ToBase64String(key.ExportRSAPrivateKey(), Base64FormattingOptions.InsertLineBreaks));
        sb.AppendLine("-----END RSA PRIVATE KEY-----");
        return sb.ToString();
    }

    private static bool ValidateCertificateChain(X509Certificate2 cert, X509Certificate2 caCert)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);

        return chain.Build(cert);
    }

    private static string FormatSerialNumber(byte[] serialBytes)
    {
        return BitConverter.ToString(serialBytes).Replace("-", ":");
    }

    private static string? GetRdnValue(string distinguishedName, string rdnType)
    {
        // Parse "CN=value, O=org, OU=unit" format
        var parts = distinguishedName.Split(',', StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var equalsIndex = part.IndexOf('=');
            if (equalsIndex > 0)
            {
                var key = part[..equalsIndex].Trim();
                var value = part[(equalsIndex + 1)..].Trim();
                if (key.Equals(rdnType, StringComparison.OrdinalIgnoreCase))
                    return value;
            }
        }
        return null;
    }
}
