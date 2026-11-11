using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Task 5159: the one way a certificate thumbprint is computed and compared for devices. It is the SHA-256 hash of the
/// DER-encoded certificate as upper-case hex (what <c>DeviceCertificate.Thumbprint</c> documents). The framework's own
/// <c>X509Certificate2.Thumbprint</c> is SHA-1 and must not be stored or compared.
/// </summary>
public static class CertificateThumbprint
{
    /// <summary>Length of a SHA-256 thumbprint in hex characters.</summary>
    public const int Sha256HexLength = 64;

    public static string Sha256(X509Certificate2 certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>SHA-256 thumbprint of a PEM certificate, or null when the PEM cannot be parsed.</summary>
    public static string? Sha256OfPem(string pem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return Sha256(certificate);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
