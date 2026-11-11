using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5159 (phase 1): device login proves more than it did. A nonce works once, repeated failures lock one device
/// (not its neighbours, not an address), a certificate PEM only counts when a trusted gateway presents it, thumbprints
/// are SHA-256 everywhere, and every failure gets the same answer.
/// </summary>
public sealed class DeviceLoginHardeningTests : IDisposable
{
    private const string GatewayKey = "gateway-key-for-tests-0123456789abcdef";

    private readonly ManualTimeProvider _clock = new();
    private readonly IAMDbContext _db = new(new DbContextOptionsBuilder<IAMDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
    }

    // ----- helpers ---------------------------------------------------------------------------

    private static IConfiguration Config(params (string Key, string? Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "DEVELOPMENT_SECRET_KEY_CHANGE_IN_PRODUCTION_32_CHARS_MIN",
            ["Jwt:Issuer"] = "https://localhost:5001",
            ["Jwt:Audience"] = "iam-api",
            ["DeviceAuth:GatewayKeys:0"] = GatewayKey,
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private (DeviceAuthenticationService Service, DeviceLoginGuard Guard) Build(IConfiguration? configuration = null)
    {
        var config = configuration ?? Config();
        var guard = new DeviceLoginGuard(_cache, config, _clock);
        var service = new DeviceAuthenticationService(
            _db, config, guard, new DeviceGatewayVerifier(config), NullLogger<DeviceAuthenticationService>.Instance);
        return (service, guard);
    }

    private Device AddHmacDevice(string deviceId, string secret, bool active = true)
    {
        var device = new Device
        {
            DeviceId = deviceId,
            Name = deviceId,
            DeviceType = "sensor",
            AuthenticationMethod = "hmac",
            TenantId = Guid.NewGuid(),
            ResourcePath = "acme:hq:" + deviceId,
            Permissions = "[\"acme:hq:" + deviceId + ":telemetry:write\"]",
            SharedSecretHash = BCrypt.Net.BCrypt.HashPassword(secret, workFactor: 4),
            IsActive = active,
        };
        _db.Devices.Add(device);
        _db.SaveChanges();
        return device;
    }

    private static string Password(string secret, string nonce, long? timestamp = null) =>
        $"{timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{nonce}:{secret}";

    private static string NewNonce() => Guid.NewGuid().ToString("N")[..16];

    private static X509Certificate2 NewCertificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    private Device AddCertificateDevice(string deviceId, X509Certificate2 certificate, string? storedThumbprint = null)
    {
        var device = new Device
        {
            DeviceId = deviceId,
            Name = deviceId,
            DeviceType = "camera",
            AuthenticationMethod = "certificate",
            TenantId = Guid.NewGuid(),
            ResourcePath = "acme:hq:" + deviceId,
            Permissions = "[]",
        };
        device.Certificates.Add(new DeviceCertificate
        {
            DeviceId = device.Id,
            SerialNumber = certificate.SerialNumber,
            Thumbprint = storedThumbprint ?? CertificateThumbprint.Sha256(certificate),
            SubjectName = certificate.Subject,
            IssuerName = certificate.Issuer,
            CertificatePem = certificate.ExportCertificatePem(),
            NotBefore = certificate.NotBefore.ToUniversalTime(),
            NotAfter = certificate.NotAfter.ToUniversalTime(),
            Status = "Active",
        });
        _db.Devices.Add(device);
        _db.SaveChanges();
        return device;
    }

    // ----- HMAC login still works ------------------------------------------------------------

    [Fact]
    public async Task Hmac_CorrectSecret_GivesAToken()
    {
        var (service, _) = Build();
        AddHmacDevice("dev-1", "s3cret-one");

        var result = await service.AuthenticateWithHmacAsync("dev-1", Password("s3cret-one", NewNonce()));

        Assert.True(result.Success);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.Equal("dev-1", result.DeviceId);
    }

    // ----- replay ----------------------------------------------------------------------------

    [Fact]
    public async Task Hmac_ReplayedRequest_IsRejected_ButAFreshNonceStillWorks()
    {
        var (service, _) = Build();
        AddHmacDevice("dev-1", "s3cret-one");
        var request = Password("s3cret-one", NewNonce());

        var first = await service.AuthenticateWithHmacAsync("dev-1", request);
        var replay = await service.AuthenticateWithHmacAsync("dev-1", request);
        var fresh = await service.AuthenticateWithHmacAsync("dev-1", Password("s3cret-one", NewNonce()));

        Assert.True(first.Success);
        Assert.False(replay.Success);
        Assert.Null(replay.AccessToken);
        Assert.True(fresh.Success);
    }

    [Fact]
    public void Hmac_TwoSimultaneousReplays_OnlyOneGetsAToken()
    {
        // The guard decides, atomically, who spends the nonce; a plain "seen it?" read followed by a write would let both in.
        var guard = new DeviceLoginGuard(_cache, Config(), _clock);
        var winners = Enumerable.Range(0, 32).AsParallel().WithDegreeOfParallelism(8)
            .Count(_ => guard.TryUseNonce("dev-1", "same-nonce"));

        Assert.Equal(1, winners);
    }

    [Fact]
    public async Task Hmac_ANonceIsOnlySpentByARequestThatKnowsTheSecret()
    {
        var (service, _) = Build();
        AddHmacDevice("dev-1", "s3cret-one");
        var nonce = NewNonce();

        var outsider = await service.AuthenticateWithHmacAsync("dev-1", Password("guess", nonce));
        var owner = await service.AuthenticateWithHmacAsync("dev-1", Password("s3cret-one", nonce));

        Assert.False(outsider.Success);
        Assert.True(owner.Success);
    }

    [Fact]
    public void Nonce_IsPerDevice_AndForgottenAfterItsLifetime()
    {
        var guard = new DeviceLoginGuard(_cache, Config(), _clock);

        Assert.True(guard.TryUseNonce("dev-1", "n"));
        Assert.True(guard.TryUseNonce("dev-2", "n"));
        Assert.False(guard.TryUseNonce("dev-1", "n"));

        // Longer than a timestamp stays acceptable (5 minutes either side), so the replay window is closed for good.
        _clock.Advance(TimeSpan.FromMinutes(5 + 5) + TimeSpan.FromSeconds(59));
        Assert.False(guard.TryUseNonce("dev-1", "n"));
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(guard.TryUseNonce("dev-1", "n"));
    }

    [Fact]
    public void Nonce_KeyCannotCollideAcrossTheDeviceNonceBoundary()
    {
        var guard = new DeviceLoginGuard(_cache, Config(), _clock);

        Assert.True(guard.TryUseNonce("ab", "c"));
        Assert.True(guard.TryUseNonce("a", "bc"));
    }

    // ----- lockout ---------------------------------------------------------------------------

    [Fact]
    public async Task Lockout_LocksOneDevice_NotItsNeighbour_AndExpires()
    {
        var (service, guard) = Build();
        AddHmacDevice("dev-a", "secret-a");
        AddHmacDevice("dev-b", "secret-b");

        for (var i = 0; i < DeviceLoginGuard.DefaultMaxFailedAttempts; i++)
        {
            Assert.False((await service.AuthenticateWithHmacAsync("dev-a", Password("wrong", NewNonce()))).Success);
        }

        Assert.True(guard.IsLockedOut("dev-a"));
        Assert.False(guard.IsLockedOut("dev-b"));

        // Locked: even the right secret gets nothing, but the neighbour logs in as usual.
        Assert.False((await service.AuthenticateWithHmacAsync("dev-a", Password("secret-a", NewNonce()))).Success);
        Assert.True((await service.AuthenticateWithHmacAsync("dev-b", Password("secret-b", NewNonce()))).Success);

        // The lock runs out on its own.
        _clock.Advance(TimeSpan.FromMinutes(DeviceLoginGuard.DefaultLockoutMinutes) + TimeSpan.FromSeconds(1));
        Assert.False(guard.IsLockedOut("dev-a"));
        Assert.True((await service.AuthenticateWithHmacAsync("dev-a", Password("secret-a", NewNonce()))).Success);
    }

    [Fact]
    public async Task Lockout_IsNotExtendedByAttemptsDuringTheLock()
    {
        var (service, guard) = Build();
        AddHmacDevice("dev-a", "secret-a");
        for (var i = 0; i < DeviceLoginGuard.DefaultMaxFailedAttempts; i++)
        {
            await service.AuthenticateWithHmacAsync("dev-a", Password("wrong", NewNonce()));
        }

        _clock.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 20; i++)
        {
            await service.AuthenticateWithHmacAsync("dev-a", Password("wrong", NewNonce()));
        }

        _clock.Advance(TimeSpan.FromMinutes(DeviceLoginGuard.DefaultLockoutMinutes - 10) + TimeSpan.FromSeconds(1));
        Assert.False(guard.IsLockedOut("dev-a"));
    }

    [Fact]
    public async Task Lockout_ASuccessfulLoginClearsTheCount_AndOldFailuresAgeOut()
    {
        var (service, guard) = Build();
        AddHmacDevice("dev-a", "secret-a");
        var almost = DeviceLoginGuard.DefaultMaxFailedAttempts - 1;

        for (var i = 0; i < almost; i++) await service.AuthenticateWithHmacAsync("dev-a", Password("wrong", NewNonce()));
        Assert.True((await service.AuthenticateWithHmacAsync("dev-a", Password("secret-a", NewNonce()))).Success);
        for (var i = 0; i < almost; i++) await service.AuthenticateWithHmacAsync("dev-a", Password("wrong", NewNonce()));
        Assert.False(guard.IsLockedOut("dev-a"));

        // Failures older than the window do not add up with new ones.
        _clock.Advance(TimeSpan.FromMinutes(DeviceLoginGuard.DefaultFailureWindowMinutes) + TimeSpan.FromSeconds(1));
        for (var i = 0; i < almost; i++) await service.AuthenticateWithHmacAsync("dev-a", Password("wrong", NewNonce()));
        Assert.False(guard.IsLockedOut("dev-a"));
    }

    [Fact]
    public async Task Lockout_ConfiguredLimitsApply()
    {
        var (service, guard) = Build(Config(("DeviceAuth:MaxFailedAttempts", "2"), ("DeviceAuth:LockoutMinutes", "1")));
        AddHmacDevice("dev-a", "secret-a");

        await service.AuthenticateWithHmacAsync("dev-a", Password("wrong", NewNonce()));
        Assert.False(guard.IsLockedOut("dev-a"));
        await service.AuthenticateWithHmacAsync("dev-a", Password("wrong", NewNonce()));
        Assert.True(guard.IsLockedOut("dev-a"));

        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(guard.IsLockedOut("dev-a"));
    }

    [Fact]
    public async Task Lockout_UnknownDeviceIds_AreNeverTracked()
    {
        var (service, guard) = Build();

        for (var i = 0; i < DeviceLoginGuard.DefaultMaxFailedAttempts + 3; i++)
        {
            await service.AuthenticateWithHmacAsync("no-such-device", Password("x", NewNonce()));
        }

        // Nothing is stored for ids that do not exist, so invented ids cannot grow the state.
        Assert.False(guard.IsLockedOut("no-such-device"));
    }

    // ----- identical answers -----------------------------------------------------------------

    [Fact]
    public async Task EveryKindOfFailure_GetsTheSameAnswer()
    {
        var (service, _) = Build();
        AddHmacDevice("dev-a", "secret-a");
        AddHmacDevice("dev-locked", "secret-locked");
        AddHmacDevice("dev-off", "secret-off", active: false);
        var cert = NewCertificate("cert-device");
        AddCertificateDevice("dev-cert", cert);
        for (var i = 0; i < DeviceLoginGuard.DefaultMaxFailedAttempts; i++)
        {
            await service.AuthenticateWithHmacAsync("dev-locked", Password("wrong", NewNonce()));
        }

        var stale = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds();
        var failures = new[]
        {
            await service.AuthenticateWithHmacAsync("no-such-device", Password("x", NewNonce())),
            await service.AuthenticateWithHmacAsync("dev-a", Password("wrong", NewNonce())),
            await service.AuthenticateWithHmacAsync("dev-locked", Password("secret-locked", NewNonce())),
            await service.AuthenticateWithHmacAsync("dev-off", Password("secret-off", NewNonce())),
            await service.AuthenticateWithHmacAsync("dev-cert", Password("x", NewNonce())),
            await service.AuthenticateWithHmacAsync("dev-a", Password("secret-a", NewNonce(), stale)),
            await service.AuthenticateWithHmacAsync("dev-a", "not-a-valid-password"),
            await service.AuthenticateWithHmacAsync("dev-a", "abc:" + NewNonce() + ":secret-a"),
            await service.AuthenticateWithHmacAsync("dev-a", ""),
            await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", null),
            await service.AuthenticateWithCertificateAsync("garbage", "dev-cert", GatewayKey),
            await service.AuthenticateWithCertificateAsync(NewCertificate("other").ExportCertificatePem(), "dev-cert", GatewayKey),
            await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "no-such-device", GatewayKey),
        };

        Assert.All(failures, f =>
        {
            Assert.False(f.Success);
            Assert.Null(f.AccessToken);
            Assert.Equal(DeviceAuthenticationService.GenericFailure, f.Error);
            Assert.Empty(f.Permissions);
            Assert.Null(f.DeviceId);
        });
    }

    // ----- certificate login: trusted gateway only -------------------------------------------

    [Fact]
    public async Task Certificate_BarePem_FromAnUntrustedCaller_GivesNoToken()
    {
        var (service, _) = Build();
        var cert = NewCertificate("cert-device");
        AddCertificateDevice("dev-cert", cert);
        var pem = cert.ExportCertificatePem(); // public: anyone who has seen the certificate has this

        Assert.False((await service.AuthenticateWithCertificateAsync(pem, "dev-cert", null)).Success);
        Assert.False((await service.AuthenticateWithCertificateAsync(pem, "dev-cert", "")).Success);
        Assert.False((await service.AuthenticateWithCertificateAsync(pem, "dev-cert", "a-guess-that-is-wrong-0123456789abcdef")).Success);
    }

    [Fact]
    public async Task Certificate_FromATrustedGateway_GivesAToken()
    {
        var (service, _) = Build();
        var cert = NewCertificate("cert-device");
        AddCertificateDevice("dev-cert", cert);

        var result = await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", GatewayKey);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
    }

    [Fact]
    public async Task Certificate_WithNoGatewayKeyConfigured_IsDisabledEntirely()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "DEVELOPMENT_SECRET_KEY_CHANGE_IN_PRODUCTION_32_CHARS_MIN",
        }).Build();
        var (service, _) = Build(config);
        var cert = NewCertificate("cert-device");
        AddCertificateDevice("dev-cert", cert);

        Assert.False((await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", GatewayKey)).Success);
        Assert.False((await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", "")).Success);
    }

    [Theory]
    [InlineData("short-key")]
    [InlineData("")]
    public async Task Certificate_AWeakConfiguredKey_IsIgnored(string weak)
    {
        var config = Config(("DeviceAuth:GatewayKeys:0", weak), ("DeviceAuth:GatewayKey", weak));
        var (service, _) = Build(config);
        var cert = NewCertificate("cert-device");
        AddCertificateDevice("dev-cert", cert);

        Assert.False((await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", weak)).Success);
    }

    [Fact]
    public async Task Certificate_ASecondKey_AllowsRotation()
    {
        var next = "next-gateway-key-0123456789abcdefABCDEF";
        var (service, _) = Build(Config(("DeviceAuth:GatewayKeys:1", next)));
        var cert = NewCertificate("cert-device");
        AddCertificateDevice("dev-cert", cert);

        Assert.True((await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", GatewayKey)).Success);
        Assert.True((await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", next)).Success);
    }

    [Fact]
    public async Task Certificate_UntrustedAttempts_CannotLockTheDevice()
    {
        var (service, guard) = Build();
        var cert = NewCertificate("cert-device");
        AddCertificateDevice("dev-cert", cert);

        for (var i = 0; i < DeviceLoginGuard.DefaultMaxFailedAttempts * 3; i++)
        {
            await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", null);
        }

        Assert.False(guard.IsLockedOut("dev-cert"));
        Assert.True((await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", GatewayKey)).Success);
    }

    [Fact]
    public async Task Certificate_RepeatedFailuresFromTheGateway_LockThatDeviceOnly()
    {
        var (service, guard) = Build();
        var good = NewCertificate("good");
        AddCertificateDevice("dev-cert", good);
        var other = NewCertificate("other");
        AddCertificateDevice("dev-neighbour", other);
        var stranger = NewCertificate("stranger").ExportCertificatePem();

        for (var i = 0; i < DeviceLoginGuard.DefaultMaxFailedAttempts; i++)
        {
            await service.AuthenticateWithCertificateAsync(stranger, "dev-cert", GatewayKey);
        }

        Assert.True(guard.IsLockedOut("dev-cert"));
        Assert.False((await service.AuthenticateWithCertificateAsync(good.ExportCertificatePem(), "dev-cert", GatewayKey)).Success);
        Assert.True((await service.AuthenticateWithCertificateAsync(other.ExportCertificatePem(), "dev-neighbour", GatewayKey)).Success);
    }

    // ----- thumbprints -----------------------------------------------------------------------

    [Fact]
    public async Task Certificate_ASha1ThumbprintOnFile_NeverMatches_UntilBackfilled()
    {
        var (service, _) = Build();
        var cert = NewCertificate("cert-device");
        AddCertificateDevice("dev-cert", cert, storedThumbprint: cert.Thumbprint); // what the CA stored before 5159 (SHA-1)

        Assert.Equal(40, cert.Thumbprint.Length);
        Assert.False((await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", GatewayKey)).Success);

        var (updated, unreadable) = await _db.BackfillSha256Async();
        await _db.SaveChangesAsync();

        Assert.Equal(1, updated);
        Assert.Equal(0, unreadable);
        Assert.Equal(CertificateThumbprint.Sha256(cert), (await _db.DeviceCertificates.SingleAsync()).Thumbprint);
        Assert.True((await service.AuthenticateWithCertificateAsync(cert.ExportCertificatePem(), "dev-cert", GatewayKey)).Success);

        // A second run has nothing left to change.
        var again = await _db.BackfillSha256Async();
        Assert.Equal((0, 0), again);
    }

    [Fact]
    public async Task Backfill_LeavesAnUnreadablePemAlone_AndReportsIt()
    {
        var cert = NewCertificate("cert-device");
        var device = AddCertificateDevice("dev-cert", cert, storedThumbprint: cert.Thumbprint);
        var row = device.Certificates.Single();
        var tracked = await _db.DeviceCertificates.SingleAsync(c => c.Id == row.Id);
        tracked.CertificatePem = "not a certificate";
        await _db.SaveChangesAsync();

        var (updated, unreadable) = await _db.BackfillSha256Async();

        Assert.Equal(0, updated);
        Assert.Equal(1, unreadable);
        Assert.Equal(cert.Thumbprint, (await _db.DeviceCertificates.SingleAsync()).Thumbprint);
    }

    [Fact]
    public async Task CertificateIssuedByTheCa_StoresASha256Thumbprint_AndCanLogIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ca5159-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = Config();
            var settings = new CertificateAuthoritySettings(Path.Combine(dir, "ca.pfx"), "ca-password-for-5159-tests");
            var ca = new CertificateAuthorityService(_db, config, settings, NullLogger<CertificateAuthorityService>.Instance);
            var device = AddCertificateDevice("dev-ca", NewCertificate("placeholder"));
            _db.DeviceCertificates.RemoveRange(_db.DeviceCertificates);
            await _db.SaveChangesAsync();

            var (issued, _) = await ca.IssueCertificateAsync(device.Id,
                new IAM.Core.Services.CertificateRequest { CommonName = "dev-ca", KeySizeInBits = 2048, ValidityDays = 30 });

            Assert.Equal(CertificateThumbprint.Sha256OfPem(issued.CertificatePem), issued.Thumbprint);
            Assert.Equal(64, issued.Thumbprint.Length);

            // The CA's own validation finds the row it stored, so a revoked certificate is seen as revoked.
            await ca.RevokeCertificateAsync(issued.Id, "test");
            var validation = await ca.ValidateCertificateAsync(issued.CertificatePem);
            Assert.True(validation.IsRevoked);

            // And a fresh one logs in through the gateway.
            var (second, _) = await ca.IssueCertificateAsync(device.Id,
                new IAM.Core.Services.CertificateRequest { CommonName = "dev-ca", KeySizeInBits = 2048, ValidityDays = 30 });
            var (service, _) = Build();
            var login = await service.AuthenticateWithCertificateAsync(second.CertificatePem, "dev-ca", GatewayKey);
            Assert.True(login.Success);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
