using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IAM.API.Authorization;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4718: claims / authorize / heartbeat / authorize-mqtt need a device token for the same device
/// (or a gateway credential, or the broker secret), and /api/mqtt/* fails closed without a broker secret.
/// </summary>
public class DeviceEndpointAuthenticationTests : IDisposable
{
    private const string BrokerSecret = "broker-secret-for-tests-0123456789";

    private readonly WebApplicationFactory<Program> _factory = new IAMTestWebApplicationFactory()
        .WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Mqtt:BrokerSecret"] = BrokerSecret })));

    public void Dispose() => _factory.Dispose();

    private async Task<(string DeviceId, Guid InternalId)> CreateDeviceAsync(Guid tenantId, string type = "sensor")
    {
        var deviceId = $"dev-{Guid.NewGuid():N}";
        var internalId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        if (!db.Tenants.Any(t => t.Id == tenantId))
            db.Tenants.Add(new Tenant { Id = tenantId, Name = $"t-{tenantId:N}", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.Devices.Add(new Device
        {
            Id = internalId,
            DeviceId = deviceId,
            Name = deviceId,
            DeviceType = type,
            TenantId = tenantId,
            ResourcePath = $"org:{deviceId}",
            Permissions = "[\"org:" + deviceId + ":read\"]"
        });
        await db.SaveChangesAsync();
        return (deviceId, internalId);
    }

    private HttpClient Client(string? token = null, string? brokerSecret = null)
    {
        var client = _factory.CreateClient();
        if (token != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (brokerSecret != null) client.DefaultRequestHeaders.Add(BrokerSecretValidator.HeaderName, brokerSecret);
        return client;
    }

    private static string DeviceToken(string deviceId, Guid internalId, Guid tenant, string type = "sensor") =>
        TestAuthenticationHelper.GenerateDeviceTokenOfType(type, internalId, deviceId, tenant);

    private static Task<HttpResponseMessage> Call(HttpClient c, string endpoint, string deviceId) => endpoint switch
    {
        "claims" => c.GetAsync($"/api/device-auth/claims/{deviceId}"),
        "heartbeat" => c.PostAsJsonAsync("/api/device-auth/heartbeat", new { deviceId }),
        "authorize" => c.PostAsJsonAsync("/api/device-auth/authorize", new { deviceId, resource = "org:x", action = "read" }),
        "authorize-mqtt" => c.PostAsJsonAsync("/api/device-auth/authorize-mqtt", new { deviceId, topic = "t/1", action = "publish" }),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
    };

    public static TheoryData<string> Endpoints => new() { "claims", "heartbeat", "authorize", "authorize-mqtt" };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Anonymous_IsUnauthorized(string endpoint)
    {
        var tenant = Guid.NewGuid();
        var (device, _) = await CreateDeviceAsync(tenant);
        var response = await Call(Client(), endpoint, device);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task TokenOfAnotherDevice_IsForbidden(string endpoint)
    {
        var tenant = Guid.NewGuid();
        var (target, _) = await CreateDeviceAsync(tenant);
        var (other, otherId) = await CreateDeviceAsync(tenant);
        var response = await Call(Client(DeviceToken(other, otherId, tenant)), endpoint, target);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task TokenOfTheSameDevice_Works(string endpoint)
    {
        var tenant = Guid.NewGuid();
        var (device, id) = await CreateDeviceAsync(tenant);
        var response = await Call(Client(DeviceToken(device, id, tenant)), endpoint, device);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("claims")]
    [InlineData("heartbeat")]
    [InlineData("authorize")]
    public async Task UserToken_IsForbidden(string endpoint)
    {
        var (device, _) = await CreateDeviceAsync(Guid.NewGuid());
        var response = await Call(Client(TestAuthenticationHelper.GenerateAdminToken()), endpoint, device);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GatewayDeviceToken_ReadsClaimsOfSameTenantDevice_NotOtherTenant()
    {
        var tenant = Guid.NewGuid();
        var (target, _) = await CreateDeviceAsync(tenant);
        var (foreign, _) = await CreateDeviceAsync(Guid.NewGuid());
        var (gw, gwId) = await CreateDeviceAsync(tenant, "gateway");
        var client = Client(DeviceToken(gw, gwId, tenant, "gateway"));

        Assert.Equal(HttpStatusCode.OK, (await Call(client, "claims", target)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(client, "claims", foreign)).StatusCode);
    }

    [Fact]
    public async Task NonGatewayDeviceToken_CannotReadOtherDeviceInSameTenant()
    {
        var tenant = Guid.NewGuid();
        var (target, _) = await CreateDeviceAsync(tenant);
        var (peer, peerId) = await CreateDeviceAsync(tenant, "sensor");
        var response = await Call(Client(DeviceToken(peer, peerId, tenant, "sensor")), "claims", target);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GatewayServiceAccount_WithPermission_Works_WithoutIt_IsForbidden()
    {
        var (device, _) = await CreateDeviceAsync(Guid.NewGuid());
        var allowed = TestAuthenticationHelper.GenerateServiceAccountToken("gw-client", DeviceEndpointAuthorizer.GatewayPermission);
        var denied = TestAuthenticationHelper.GenerateServiceAccountToken("other-client", "users:create");

        Assert.Equal(HttpStatusCode.OK, (await Call(Client(allowed), "claims", device)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(Client(denied), "claims", device)).StatusCode);
    }

    [Fact]
    public async Task AuthorizeMqtt_BrokerSecret_Works_WrongSecret_IsUnauthorized()
    {
        var (device, _) = await CreateDeviceAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, (await Call(Client(brokerSecret: BrokerSecret), "authorize-mqtt", device)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(Client(brokerSecret: "wrong"), "authorize-mqtt", device)).StatusCode);
    }

    [Fact]
    public async Task TokenIssuingRoutes_StayAnonymous()
    {
        var client = Client();
        var hmac = await client.PostAsJsonAsync("/api/device-auth/hmac", new { deviceId = "nobody", password = "1:2:3" });
        var cert = await client.PostAsJsonAsync("/api/device-auth/certificate", new { deviceId = "nobody", certificatePem = "x" });
        Assert.NotEqual(HttpStatusCode.Forbidden, hmac.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, cert.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, hmac.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, cert.StatusCode);
    }

    // ----- MQTT broker endpoints ---------------------------------------------------------------

    [Fact]
    public async Task Mqtt_WithSecretConfigured_WrongOrMissingSecret_Is403_RightSecret_Works()
    {
        var body = new { clientId = "c1", username = "u", password = "p" };

        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsJsonAsync("/api/mqtt/auth", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(brokerSecret: "wrong").PostAsJsonAsync("/api/mqtt/auth", body)).StatusCode);
        // A same-length near miss must not pass either.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client(brokerSecret: BrokerSecret[..^1] + "X").PostAsJsonAsync("/api/mqtt/auth", body)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Client().GetAsync("/api/mqtt/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(brokerSecret: BrokerSecret).GetAsync("/api/mqtt/stats")).StatusCode);
    }

    [Fact]
    public async Task Mqtt_NoSecretConfigured_InProduction_RefusesEveryCall()
    {
        // A full Production boot needs signing certificates; swapping the injected IHostEnvironment is enough
        // to exercise the controller's fail-closed path.
        using var production = new IAMTestWebApplicationFactory().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Mqtt:BrokerSecret"] = "" }));
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostEnvironment>();
                services.AddSingleton<IHostEnvironment>(new FakeEnv("Production"));
            });
        });
        var client = production.CreateClient();
        var body = new { clientId = "c1", username = "u", password = "p" };

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/mqtt/auth", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/mqtt/stats")).StatusCode);
    }

    // ----- validator unit tests ----------------------------------------------------------------

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static HttpRequest RequestWith(string? secret)
    {
        var ctx = new DefaultHttpContext();
        if (secret != null) ctx.Request.Headers[BrokerSecretValidator.HeaderName] = secret;
        return ctx.Request;
    }

    private static IConfiguration Config(string? secret) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mqtt:BrokerSecret"] = secret }).Build();

    [Theory]
    [InlineData("Production", null, null, BrokerSecretResult.NotConfigured)]
    [InlineData("Production", "", "anything", BrokerSecretResult.NotConfigured)]
    [InlineData("Development", null, null, BrokerSecretResult.Accepted)]
    [InlineData("Production", "s3cret", "s3cret", BrokerSecretResult.Accepted)]
    [InlineData("Production", "s3cret", "s3cre", BrokerSecretResult.Rejected)]
    [InlineData("Development", "s3cret", null, BrokerSecretResult.Rejected)]
    public void Validator_Matrix(string env, string? configured, string? provided, BrokerSecretResult expected)
    {
        Assert.Equal(expected, BrokerSecretValidator.Check(Config(configured), new FakeEnv(env), RequestWith(provided)));
    }
}
