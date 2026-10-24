using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 3314 (follow-up to 3160/PR #110): AddEphemeralEncryptionKey() regenerates its RSA key
/// in memory on every process start, so every refresh token issued before a restart becomes
/// permanently undecryptable on the next one. These tests exercise the real OpenIddict
/// AddEncryptionCertificate API and the real RSA-OAEP/JWE mechanism it relies on directly
/// (no ASP.NET Core host), rather than through IAMTestWebApplicationFactory — that factory's
/// WebApplicationFactory/HostFactoryResolver pipeline currently fails all controller tests on
/// this environment with "JWT secret key not configured" even on an unmodified checkout of
/// develop (confirmed via `git diff origin/develop` showing zero changes to Program.cs or the
/// factory), a pre-existing, unrelated environment regression - not something this task's
/// scope covers.
/// </summary>
public class OpenIddictEncryptionCertificateTests
{
    private static X509Certificate2 CreateSelfSignedCertificate(string subject, X509KeyUsageFlags keyUsage)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage, critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void AddEncryptionCertificate_RejectsDigitalSignatureOnlyCertificate()
    {
        // Regression guard for the crash PR #110 fixed: signing.pfx (DigitalSignature only,
        // no KeyEncipherment) throws when registered as an encryption certificate.
        using var signingShapedCert = CreateSelfSignedCertificate("CN=Test Signing Only", X509KeyUsageFlags.DigitalSignature);

        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenIddict().AddServer(options => options.AddEncryptionCertificate(signingShapedCert)));

        Assert.Contains("key encryption certificate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddEncryptionCertificate_AcceptsKeyEnciphermentCertificate()
    {
        // The fix: a dedicated cert with KeyEncipherment usage is accepted by OpenIddict.
        using var encryptionCert = CreateSelfSignedCertificate("CN=Test Encryption", X509KeyUsageFlags.KeyEncipherment);

        var services = new ServiceCollection();
        var exception = Record.Exception(() =>
            services.AddOpenIddict().AddServer(options => options.AddEncryptionCertificate(encryptionCert)));

        Assert.Null(exception);
    }

    private static string EncryptToken(X509Certificate2 cert)
    {
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "https://iam.example.com",
            Subject = new ClaimsIdentity(new[] { new Claim("sub", "restart-test-user") }),
            Expires = DateTime.UtcNow.AddDays(7),
            EncryptingCredentials = new X509EncryptingCredentials(cert, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512),
        };
        return handler.CreateEncodedJwt(descriptor);
    }

    private static bool TryDecryptToken(string jwe, X509Certificate2 cert)
    {
        var handler = new JwtSecurityTokenHandler();
        try
        {
            handler.ValidateToken(jwe, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
                ValidateIssuerSigningKey = false,
                RequireSignedTokens = false,
                TokenDecryptionKey = new X509SecurityKey(cert),
            }, out _);
            return true;
        }
        catch (SecurityTokenException)
        {
            return false;
        }
    }

    [Fact]
    public void EncryptedToken_SurvivesRestart_WhenUsingPersistedCertificate()
    {
        // The actual acceptance criterion: a refresh token encrypted by one process instance
        // must still decrypt after a restart. Simulated here by loading the SAME on-disk PFX
        // independently twice (X509CertificateLoader.LoadPkcs12FromFile), exactly like
        // Program.cs does on every process start - each load is a fresh, independent RSA key
        // object, so success here proves persistence-across-restart, not just in-memory reuse.
        using var originalCert = CreateSelfSignedCertificate("CN=Test Persisted Encryption", X509KeyUsageFlags.KeyEncipherment);
        var pfxBytes = originalCert.Export(X509ContentType.Pfx, "test-password");

        var tempPath = Path.Combine(Path.GetTempPath(), $"iam-test-enc-{Guid.NewGuid()}.pfx");
        try
        {
            File.WriteAllBytes(tempPath, pfxBytes);

            using var instanceA = X509CertificateLoader.LoadPkcs12FromFile(tempPath, "test-password");
            var jwe = EncryptToken(instanceA);

            using var instanceB = X509CertificateLoader.LoadPkcs12FromFile(tempPath, "test-password"); // fresh, independent load
            Assert.True(TryDecryptToken(jwe, instanceB), "Token encrypted by instance A must decrypt with instance B loaded from the same persisted certificate file.");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void EncryptedToken_DoesNotSurviveRestart_WhenUsingEphemeralKey()
    {
        // Documents/pins the exact bug being fixed: AddEphemeralEncryptionKey() generates a new
        // RSA key on every process start, so a token encrypted by one instance can never be
        // decrypted by another. If this assertion ever starts failing, the methodology used by
        // EncryptedToken_SurvivesRestart_WhenUsingPersistedCertificate above would be unsound.
        using var ephemeralA = CreateSelfSignedCertificate("CN=Test Ephemeral A", X509KeyUsageFlags.KeyEncipherment);
        var jwe = EncryptToken(ephemeralA);

        using var ephemeralB = CreateSelfSignedCertificate("CN=Test Ephemeral B", X509KeyUsageFlags.KeyEncipherment);
        Assert.False(TryDecryptToken(jwe, ephemeralB), "A token encrypted with one ephemeral key must NOT decrypt with an independently-generated ephemeral key - this is the bug task 3314 fixes.");
    }
}
