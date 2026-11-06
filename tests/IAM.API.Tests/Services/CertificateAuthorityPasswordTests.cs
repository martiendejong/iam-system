using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5165: the CA key file password must be configured outside Development (no hardcoded default), a key file
/// made with an older version's password is re-protected once without changing the CA, and a wrong password is a
/// clear failure that never touches the file.
/// </summary>
public sealed class CertificateAuthorityPasswordTests : IDisposable
{
    private const string Configured = "configured-ca-password-5165";
    private const string Legacy = "legacy-password-made-by-an-older-version";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ca5165-" + Guid.NewGuid().ToString("N"));

    public CertificateAuthorityPasswordTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PfxPath => Path.Combine(_dir, "ca.pfx");

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    /// <summary>Writes a self-signed CA key file protected with <paramref name="password"/>; returns its thumbprint.</summary>
    private string WriteCaFile(string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test CA 5165", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(PfxPath, cert.Export(X509ContentType.Pfx, password));
        return cert.Thumbprint;
    }

    private static bool Opens(string path, string password)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(path, password);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static CertificateAuthorityService ServiceFor(CertificateAuthoritySettings settings, IConfiguration? configuration = null)
    {
        var db = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return new CertificateAuthorityService(db, configuration ?? Config(), settings, NullLogger<CertificateAuthorityService>.Instance);
    }

    // ----- startup: a configured password is required outside Development --------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingPassword_OutsideDevelopment_StopsStartupWithAClearMessage(string? password)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CertificateAuthoritySettings.FromConfiguration(Config(("Ca:CertificatePassword", password)), isDevelopment: false));

        Assert.Contains("Ca:CertificatePassword", ex.Message);
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public void MissingPassword_FailsBeforeAnyCaFileIsRead()
    {
        // The file is garbage and the legacy location exists too: the failure must be the configuration one, which
        // proves nothing tried to open (or move) a CA file first.
        File.WriteAllText(PfxPath, "not a pfx");
        var config = Config(("Ca:CertificatePath", PfxPath));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CertificateAuthoritySettings.FromConfiguration(config, isDevelopment: false));

        Assert.Contains("not configured", ex.Message);
        Assert.Equal("not a pfx", File.ReadAllText(PfxPath));
    }

    [Fact]
    public void Development_WithoutAPassword_UsesTheDocumentedDevOnlyPassword()
    {
        var settings = CertificateAuthoritySettings.FromConfiguration(Config(), isDevelopment: true);

        Assert.Equal(CertificateAuthoritySettings.DevelopmentOnlyPassword, settings.CertificatePassword);
    }

    [Fact]
    public void ConfiguredPassword_IsUsed_InEveryEnvironment()
    {
        var config = Config(("Ca:CertificatePassword", Configured));

        Assert.Equal(Configured, CertificateAuthoritySettings.FromConfiguration(config, isDevelopment: false).CertificatePassword);
        Assert.Equal(Configured, CertificateAuthoritySettings.FromConfiguration(config, isDevelopment: true).CertificatePassword);
    }

    [Fact]
    public void DevOnlyPassword_IsNotUsedOutsideDevelopment_EvenWhenNothingElseIsConfigured()
    {
        Assert.Throws<InvalidOperationException>(() => CertificateAuthoritySettings.FromConfiguration(Config(), isDevelopment: false));
    }

    // ----- default key file location ---------------------------------------------------------------

    [Fact]
    public void DefaultPath_IsOutsideTheApplicationFolder()
    {
        var settings = CertificateAuthoritySettings.FromConfiguration(
            Config(("Ca:CertificatePassword", Configured)), isDevelopment: false,
            defaultPath: null, legacyPath: Path.Combine(_dir, "does-not-exist", "ca.pfx"));

        var full = Path.GetFullPath(settings.CertificatePath);
        Assert.True(Path.IsPathRooted(settings.CertificatePath));
        Assert.False(full.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.False(settings.UsesLegacyDefaultLocation);
    }

    [Fact]
    public void ConfiguredPath_Wins()
    {
        var settings = CertificateAuthoritySettings.FromConfiguration(
            Config(("Ca:CertificatePassword", Configured), ("Ca:CertificatePath", PfxPath)), isDevelopment: false);

        Assert.Equal(PfxPath, settings.CertificatePath);
    }

    [Fact]
    public void AnExistingKeyFileAtTheOldDefaultLocation_IsKeptInPlace_NotOrphaned()
    {
        // Moving the default must not make the app create a brand-new CA (new thumbprint) next to the real one.
        WriteCaFile(Legacy);
        var newDefault = Path.Combine(_dir, "new", "ca.pfx");

        var settings = CertificateAuthoritySettings.FromConfiguration(
            Config(("Ca:CertificatePassword", Configured)), isDevelopment: false, defaultPath: newDefault, legacyPath: PfxPath);

        Assert.Equal(PfxPath, settings.CertificatePath);
        Assert.True(settings.UsesLegacyDefaultLocation);
    }

    [Fact]
    public void WhenTheNewDefaultAlreadyExists_ItWinsOverTheOldLocation()
    {
        var newDefault = Path.Combine(_dir, "new", "ca.pfx");
        Directory.CreateDirectory(Path.GetDirectoryName(newDefault)!);
        File.WriteAllText(newDefault, "x");
        WriteCaFile(Legacy);

        var settings = CertificateAuthoritySettings.FromConfiguration(
            Config(("Ca:CertificatePassword", Configured)), isDevelopment: false, defaultPath: newDefault, legacyPath: PfxPath);

        Assert.Equal(newDefault, settings.CertificatePath);
    }

    // ----- the key file ----------------------------------------------------------------------------

    [Fact]
    public async Task NewCa_IsProtectedWithTheConfiguredPassword()
    {
        var service = ServiceFor(new CertificateAuthoritySettings(PfxPath, Configured));

        var info = await service.GetCaInfoAsync();

        Assert.True(File.Exists(PfxPath));
        Assert.True(Opens(PfxPath, Configured));
        Assert.False(Opens(PfxPath, CertificateAuthoritySettings.DevelopmentOnlyPassword));
        Assert.False(string.IsNullOrEmpty(info.Thumbprint));
    }

    [Fact]
    public async Task WrongPassword_IsAClearFailure_AndTheFileIsUntouched()
    {
        WriteCaFile("some-other-password");
        var before = File.ReadAllBytes(PfxPath);
        var service = ServiceFor(new CertificateAuthoritySettings(PfxPath, Configured));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCaInfoAsync());

        Assert.Contains("Ca:CertificatePassword", ex.Message);
        Assert.DoesNotContain(Configured, ex.Message);
        Assert.Equal(before, File.ReadAllBytes(PfxPath));
    }

    [Fact]
    public async Task OldProtectedFile_WithoutALegacyPasswordSetting_IsNeverOpened_SoThereIsNoDefaultFallback()
    {
        WriteCaFile(Legacy);
        var before = File.ReadAllBytes(PfxPath);
        var service = ServiceFor(new CertificateAuthoritySettings(PfxPath, Configured));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCaInfoAsync());

        Assert.Equal(before, File.ReadAllBytes(PfxPath));
        Assert.True(Opens(PfxPath, Legacy));
    }

    [Fact]
    public async Task OldProtectedFile_IsReProtectedOnFirstStart_AndTheThumbprintIsUnchanged()
    {
        var thumbprint = WriteCaFile(Legacy);
        var service = ServiceFor(new CertificateAuthoritySettings(PfxPath, Configured, Legacy));

        var info = await service.GetCaInfoAsync();

        Assert.Equal(thumbprint, info.Thumbprint);
        Assert.True(Opens(PfxPath, Configured));
        Assert.False(Opens(PfxPath, Legacy));
        Assert.False(File.Exists(PfxPath + ".tmp"));

        // Next start: the legacy setting is gone (and not needed any more); the same CA comes up.
        var next = await ServiceFor(new CertificateAuthoritySettings(PfxPath, Configured)).GetCaInfoAsync();
        Assert.Equal(thumbprint, next.Thumbprint);
    }

    [Fact]
    public async Task ReProtect_KeepsTheSameKey_SoOldDeviceCertificatesStillChainToTheCa()
    {
        var thumbprint = WriteCaFile(Legacy);
        byte[] signedBefore;
        using (var legacyCa = X509CertificateLoader.LoadPkcs12FromFile(PfxPath, Legacy, X509KeyStorageFlags.Exportable))
        {
            using var deviceKey = RSA.Create(2048);
            var csr = new CertificateRequest("CN=device", deviceKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var device = csr.Create(legacyCa, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30), new byte[] { 1, 2, 3, 4 });
            signedBefore = device.RawData;
        }

        var info = await ServiceFor(new CertificateAuthoritySettings(PfxPath, Configured, Legacy)).GetCaInfoAsync();

        Assert.Equal(thumbprint, info.Thumbprint);
        using var reprotected = X509CertificateLoader.LoadPkcs12FromFile(PfxPath, Configured);
        using var oldDevice = X509CertificateLoader.LoadCertificate(signedBefore);
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(reprotected);
        Assert.True(chain.Build(oldDevice));
    }

    [Fact]
    public async Task WrongLegacyPassword_IsAClearFailure_AndTheFileIsUntouched()
    {
        WriteCaFile("a-third-password");
        var before = File.ReadAllBytes(PfxPath);
        var service = ServiceFor(new CertificateAuthoritySettings(PfxPath, Configured, Legacy));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCaInfoAsync());

        Assert.Contains("Ca:LegacyCertificatePassword", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(PfxPath));
    }
}
