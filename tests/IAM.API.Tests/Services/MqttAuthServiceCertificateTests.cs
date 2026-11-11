using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>Task 5484: certificate devices must prove the certificate, not just announce a clientId.</summary>
public class MqttAuthServiceCertificateTests
{
    private sealed class HmacStub : IDeviceAuthenticationService
    {
        public Task<DeviceAuthResult> AuthenticateWithHmacAsync(string deviceId, string hmacPassword) =>
            Task.FromResult(new DeviceAuthResult { Success = hmacPassword == "good" });
        public Task<DeviceAuthResult> AuthenticateWithCertificateAsync(string certificatePem, string deviceId, string? gatewayKey) => throw new NotSupportedException();
        public Task<DeviceAuthorizationResult> AuthorizeAsync(string deviceId, string resource, string action) => throw new NotSupportedException();
        public Task<DeviceAuthorizationResult> AuthorizeMqttAsync(string deviceId, string topic, string action) => throw new NotSupportedException();
        public Task<DeviceClaimsResult> GetDeviceClaimsAsync(string deviceId) => throw new NotSupportedException();
    }

    private readonly IAMDbContext _db = new(new DbContextOptionsBuilder<IAMDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid _tenant = Guid.NewGuid();

    public MqttAuthServiceCertificateTests()
    {
        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "T", IsActive = true });
        _db.SaveChanges();
    }

    private MqttAuthService Service() => new(_db, new HmacStub(), NullLogger<MqttAuthService>.Instance);

    private Device AddDevice(string method)
    {
        var d = new Device
        {
            Id = Guid.NewGuid(), DeviceId = $"dev-{Guid.NewGuid():N}", Name = "d", DeviceType = "sensor",
            AuthenticationMethod = method, TenantId = _tenant, ResourcePath = "a:b", IsActive = true
        };
        _db.Devices.Add(d);
        _db.SaveChanges();
        return d;
    }

    private DeviceCertificate AddCert(Device d, string thumbprint, string status = "Active", int validDays = 30, string pem = "")
    {
        var c = new DeviceCertificate
        {
            DeviceId = d.Id, Thumbprint = thumbprint, SerialNumber = Guid.NewGuid().ToString("N"), Status = status,
            NotBefore = DateTime.UtcNow.AddDays(-1), NotAfter = DateTime.UtcNow.AddDays(validDays), CertificatePem = pem
        };
        _db.DeviceCertificates.Add(c);
        _db.SaveChanges();
        return c;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("certificate")]
    public async Task MatchingActiveCertificate_IsAllowed_CaseInsensitive(string? username)
    {
        var d = AddDevice("certificate");
        AddCert(d, "AABBCCDD11");
        var r = await Service().AuthenticateClientAsync(d.DeviceId, username, null, "aa:bb:cc:dd:11");
        Assert.True(r.Allowed);
    }

    [Fact]
    public async Task Sha256OfStoredPem_IsAllowed()
    {
        var d = AddDevice("certificate");
        using var rsa = RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=x", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(5));
        AddCert(d, cert.Thumbprint, pem: cert.ExportCertificatePem());
        var sha256 = Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
        Assert.True((await Service().AuthenticateClientAsync(d.DeviceId, "", null, sha256)).Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingFingerprint_IsDenied(string? fp)
    {
        var d = AddDevice("certificate");
        AddCert(d, "AABB");
        Assert.False((await Service().AuthenticateClientAsync(d.DeviceId, "", null, fp)).Allowed);
    }

    [Fact]
    public async Task FingerprintOfAnotherDevice_IsDenied()
    {
        var a = AddDevice("certificate");
        var b = AddDevice("certificate");
        AddCert(a, "AAAA");
        AddCert(b, "BBBB");
        Assert.False((await Service().AuthenticateClientAsync(a.DeviceId, "", null, "BBBB")).Allowed);
    }

    [Fact]
    public async Task RevokedCertificate_IsDenied()
    {
        var d = AddDevice("certificate");
        AddCert(d, "CCCC", status: "Revoked");
        Assert.False((await Service().AuthenticateClientAsync(d.DeviceId, "", null, "CCCC")).Allowed);
    }

    [Fact]
    public async Task ExpiredCertificate_IsDenied()
    {
        var d = AddDevice("certificate");
        AddCert(d, "DDDD", validDays: -1);
        Assert.False((await Service().AuthenticateClientAsync(d.DeviceId, "", null, "DDDD")).Allowed);
    }

    [Fact]
    public async Task Hmac_BehavesAsBefore_WithoutFingerprint()
    {
        var d = AddDevice("hmac");
        Assert.True((await Service().AuthenticateClientAsync(d.DeviceId, "user", "good", null)).Allowed);
        Assert.False((await Service().AuthenticateClientAsync(d.DeviceId, "user", "bad", null)).Allowed);
        Assert.False((await Service().AuthenticateClientAsync(d.DeviceId, "", null, null)).Allowed);
    }
}
