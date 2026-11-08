using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5159 over HTTP: /api/device-auth answers every failed login with the same 401 body, a certificate login needs
/// the trusted-gateway header, and a request that was already used cannot be sent again.
/// </summary>
public class DeviceAuthLoginTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private const string GatewayKey = "http-gateway-key-for-tests-0123456789abcdef";

    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public DeviceAuthLoginTests(IAMTestWebApplicationFactory factory)
    {
        var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["DeviceAuth:GatewayKeys:0"] = GatewayKey })));
        _client = configured.CreateClient();
        _services = configured.Services;
    }

    private async Task AddDeviceAsync(Device device)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.Devices.Add(device);
        await db.SaveChangesAsync();
    }

    private static Device HmacDevice(string id, string secret) => new()
    {
        DeviceId = id,
        Name = id,
        DeviceType = "sensor",
        AuthenticationMethod = "hmac",
        TenantId = Guid.NewGuid(),
        ResourcePath = "acme:" + id,
        SharedSecretHash = BCrypt.Net.BCrypt.HashPassword(secret, workFactor: 4),
    };

    private static string Password(string secret, string nonce) =>
        $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{nonce}:{secret}";

    private async Task<(HttpStatusCode Status, string Body)> PostHmacAsync(string deviceId, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/device-auth/hmac", new { deviceId, password });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Hmac_Login_ThenReplay_ThenWrongSecret_AndUnknownDevice_AnswerTheSameWay()
    {
        var id = "http-" + Guid.NewGuid().ToString("N")[..8];
        await AddDeviceAsync(HmacDevice(id, "http-secret"));
        var request = Password("http-secret", Guid.NewGuid().ToString("N")[..16]);

        var ok = await PostHmacAsync(id, request);
        var replay = await PostHmacAsync(id, request);
        var wrong = await PostHmacAsync(id, Password("nope", Guid.NewGuid().ToString("N")[..16]));
        var unknown = await PostHmacAsync("no-such-" + id, Password("x", Guid.NewGuid().ToString("N")[..16]));

        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Contains("accessToken", ok.Body);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Equal(replay.Body, wrong.Body);
        Assert.Equal(replay.Body, unknown.Body);
        Assert.Equal(DeviceAuthenticationService.GenericFailure, JsonDocument.Parse(wrong.Body).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Hmac_LockedDevice_AnswersLikeAWrongSecret()
    {
        var id = "lock-" + Guid.NewGuid().ToString("N")[..8];
        await AddDeviceAsync(HmacDevice(id, "lock-secret"));

        (HttpStatusCode Status, string Body) last = default;
        for (var i = 0; i < DeviceLoginGuard.DefaultMaxFailedAttempts; i++)
        {
            last = await PostHmacAsync(id, Password("nope", Guid.NewGuid().ToString("N")[..16]));
        }

        var lockedButCorrect = await PostHmacAsync(id, Password("lock-secret", Guid.NewGuid().ToString("N")[..16]));

        Assert.Equal(HttpStatusCode.Unauthorized, lockedButCorrect.Status);
        Assert.Equal(last.Body, lockedButCorrect.Body);
    }

    [Fact]
    public async Task Certificate_NeedsTheGatewayHeader()
    {
        var id = "cert-" + Guid.NewGuid().ToString("N")[..8];
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + id, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var device = new Device
        {
            DeviceId = id,
            Name = id,
            DeviceType = "camera",
            AuthenticationMethod = "certificate",
            TenantId = Guid.NewGuid(),
            ResourcePath = "acme:" + id,
        };
        device.Certificates.Add(new DeviceCertificate
        {
            DeviceId = device.Id,
            SerialNumber = certificate.SerialNumber,
            Thumbprint = CertificateThumbprint.Sha256(certificate),
            SubjectName = certificate.Subject,
            IssuerName = certificate.Issuer,
            CertificatePem = certificate.ExportCertificatePem(),
            NotBefore = certificate.NotBefore.ToUniversalTime(),
            NotAfter = certificate.NotAfter.ToUniversalTime(),
        });
        await AddDeviceAsync(device);
        var body = new { certificatePem = certificate.ExportCertificatePem(), deviceId = id };

        var bare = await _client.PostAsJsonAsync("/api/device-auth/certificate", body);

        using var wrongKey = new HttpRequestMessage(HttpMethod.Post, "/api/device-auth/certificate") { Content = JsonContent.Create(body) };
        wrongKey.Headers.Add(DeviceGatewayVerifier.HeaderName, "not-the-key-0123456789abcdef0123456789");
        var wrong = await _client.SendAsync(wrongKey);

        using var trusted = new HttpRequestMessage(HttpMethod.Post, "/api/device-auth/certificate") { Content = JsonContent.Create(body) };
        trusted.Headers.Add(DeviceGatewayVerifier.HeaderName, GatewayKey);
        var good = await _client.SendAsync(trusted);

        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.DoesNotContain("accessToken", await bare.Content.ReadAsStringAsync());
        Assert.Equal(await bare.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Contains("accessToken", await good.Content.ReadAsStringAsync());
    }
}
