using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5153: the region API (register, update, delete, health check, failover, conflict resolution and the reads that
/// show endpoints and stored error text) is for platform administrators only, and a region endpoint must be a public
/// http(s) address (the webhook rule), also when the health check runs.
/// </summary>
public class RegionAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public RegionAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(params string[] roles) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(Guid.NewGuid(), "u@region-authz.test", roles, null));

    private async Task<RegionConfig> SeedRegionAsync(string endpoint = "http://127.0.0.1:9", bool primary = false,
        RegionStatus status = RegionStatus.Active, int priority = 5)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var region = new RegionConfig
        {
            Name = $"r-{Guid.NewGuid():N}",
            Endpoint = endpoint,
            IsPrimary = primary,
            Status = status,
            Priority = priority
        };
        db.RegionConfigs.Add(region);
        await db.SaveChangesAsync();
        return region;
    }

    private async Task<RegionConfig?> RowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.RegionConfigs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    private async Task<bool> NameExistsAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.RegionConfigs.AnyAsync(r => r.Name == name);
    }

    private async Task<Guid> SeedConflictAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var e = new RegionSyncEvent
        {
            SourceRegion = "a", TargetRegion = "b", EntityType = "User", EntityId = Guid.NewGuid().ToString(),
            SyncStatus = SyncStatus.Conflict
        };
        db.RegionSyncEvents.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    private static object Body(string endpoint, string? name = null) => new
    {
        name = name ?? $"new-{Guid.NewGuid():N}",
        endpoint,
        isPrimary = false,
        status = 1,
        description = "d",
        priority = 3
    };

    private static readonly string[] CallNames =
        { "list", "get", "register", "update", "delete", "health", "failover", "sync-status", "sync-events", "resolve" };

    private static async Task<HttpStatusCode[]> AllCallsAsync(HttpClient c, Guid regionId, Guid conflictId) => new[]
    {
        (await c.GetAsync("/api/regions")).StatusCode,
        (await c.GetAsync($"/api/regions/{regionId}")).StatusCode,
        (await c.PostAsJsonAsync("/api/regions", Body("https://93.184.216.34"))).StatusCode,
        (await c.PutAsJsonAsync($"/api/regions/{regionId}", Body("https://93.184.216.34", "renamed"))).StatusCode,
        (await c.DeleteAsync($"/api/regions/{regionId}")).StatusCode,
        (await c.GetAsync($"/api/regions/{regionId}/health")).StatusCode,
        (await c.PostAsJsonAsync("/api/regions/failover", new { targetRegionId = regionId })).StatusCode,
        (await c.GetAsync("/api/regions/sync-status")).StatusCode,
        (await c.GetAsync("/api/regions/sync-events")).StatusCode,
        (await c.PostAsJsonAsync($"/api/regions/sync-events/{conflictId}/resolve", new { resolution = 1 })).StatusCode,
    };

    private static void AssertAll(HttpStatusCode expected, HttpStatusCode[] statuses)
    {
        for (var i = 0; i < statuses.Length; i++)
            Assert.True(statuses[i] == expected, $"{CallNames[i]} returned {statuses[i]}, expected {expected}");
    }

    // ----- who may call ----------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_Gets401()
    {
        var region = await SeedRegionAsync();

        AssertAll(HttpStatusCode.Unauthorized, await AllCallsAsync(_factory.CreateClient(), region.Id, await SeedConflictAsync()));
    }

    [Theory]
    [InlineData("User")]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingManager")]
    [InlineData("SecurityAdmin")]
    public async Task NonPlatformAdmin_Gets403_OnEveryAction_AndNothingChanges(string role)
    {
        var region = await SeedRegionAsync(primary: false);
        var standby = await SeedRegionAsync();
        var conflict = await SeedConflictAsync();
        var client = ClientAs(role);
        var newName = $"intruder-{Guid.NewGuid():N}";

        AssertAll(HttpStatusCode.Forbidden, await AllCallsAsync(client, region.Id, conflict));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/regions", Body("https://93.184.216.34", newName))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/regions/failover", new { })).StatusCode);

        Assert.False(await NameExistsAsync(newName));
        var row = await RowAsync(region.Id);
        Assert.Equal(region.Name, row!.Name);
        Assert.False(row.IsPrimary);
        Assert.Null(row.LastHealthCheck);
        Assert.False((await RowAsync(standby.Id))!.IsPrimary);
    }

    [Fact]
    public async Task UserCannotProbeAnInternalAddress_NothingIsRegisteredAndNoErrorTextComesBack()
    {
        var client = ClientAs("User");
        var name = $"probe-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/regions", Body("http://169.254.169.254/latest", name));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await NameExistsAsync(name));
        Assert.DoesNotContain("169.254", body);
    }

    [Fact]
    public async Task DeviceAndServiceAccountTokens_Get403()
    {
        var region = await SeedRegionAsync();
        var conflict = await SeedConflictAsync();
        var device = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(Guid.NewGuid(), "dev-1", Guid.NewGuid()));
        var service = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "regions:write"));

        AssertAll(HttpStatusCode.Forbidden, await AllCallsAsync(device, region.Id, conflict));
        AssertAll(HttpStatusCode.Forbidden, await AllCallsAsync(service, region.Id, conflict));
        Assert.NotNull(await RowAsync(region.Id));
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    public async Task PlatformAdmin_CanDoEveryRegionAction(string role)
    {
        var client = ClientAs(role);
        var primary = await SeedRegionAsync(primary: true, status: RegionStatus.Active);
        var standby = await SeedRegionAsync(status: RegionStatus.Standby);
        var other = await SeedRegionAsync();
        var conflict = await SeedConflictAsync();
        var name = $"admin-{Guid.NewGuid():N}";

        // reads
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/regions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/regions/{standby.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/regions/sync-status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/regions/sync-events")).StatusCode);
        // register / update with public endpoints
        var created = await client.PostAsJsonAsync("/api/regions", Body("https://93.184.216.34", name));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/regions/{id}", Body("https://93.184.216.35", name + "-v2"))).StatusCode);
        Assert.Equal(name + "-v2", (await RowAsync(id))!.Name);
        // health check: the endpoint of 'standby' is loopback, so it is refused by the guard, the call itself works
        var health = await client.GetAsync($"/api/regions/{standby.Id}/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.False((await health.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isHealthy").GetBoolean());
        // failover, conflict resolution, delete
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/regions/failover", new { targetRegionId = standby.Id })).StatusCode);
        Assert.True((await RowAsync(standby.Id))!.IsPrimary);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/regions/sync-events/{conflict}/resolve", new { resolution = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/regions/{other.Id}")).StatusCode);
        Assert.Null(await RowAsync(other.Id));
        Assert.NotNull(await RowAsync(primary.Id));
    }

    // ----- endpoint rules --------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:5000/")]
    [InlineData("http://localhost")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://169.254.10.20")]
    [InlineData("http://10.0.0.5")]
    [InlineData("https://192.168.1.10")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://[::1]")]
    [InlineData("http://[fe80::1]")]
    [InlineData("http://2130706433")]
    [InlineData("ftp://93.184.216.34")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://93.184.216.34")]
    [InlineData("not a url")]
    [InlineData("https://user:pass@93.184.216.34")]
    public async Task RegisterAndUpdate_RejectNonPublicOrNonHttpEndpoints_WithAClearError(string endpoint)
    {
        var client = ClientAs("SuperAdmin");
        var existing = await SeedRegionAsync("https://93.184.216.34");
        var name = $"bad-{Guid.NewGuid():N}";

        var register = await client.PostAsJsonAsync("/api/regions", Body(endpoint, name));
        var update = await client.PutAsJsonAsync($"/api/regions/{existing.Id}", Body(endpoint, "changed"));

        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Contains("Region endpoint rejected", await register.Content.ReadAsStringAsync());
        Assert.Contains("Region endpoint rejected", await update.Content.ReadAsStringAsync());
        Assert.False(await NameExistsAsync(name));
        var row = await RowAsync(existing.Id);
        Assert.Equal("https://93.184.216.34", row!.Endpoint);
        Assert.Equal(existing.Name, row.Name);
    }

    [Theory]
    [InlineData("https://93.184.216.34")]
    [InlineData("http://93.184.216.34:8080/base")]
    [InlineData("https://[2606:4700:4700::1111]")]
    public async Task RegisterAndUpdate_AcceptPublicHttpEndpoints(string endpoint)
    {
        var client = ClientAs("SystemAdmin");
        var existing = await SeedRegionAsync("https://93.184.216.34");

        var register = await client.PostAsJsonAsync("/api/regions", Body(endpoint));
        var update = await client.PutAsJsonAsync($"/api/regions/{existing.Id}", Body(endpoint, "changed"));

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(endpoint, (await RowAsync(existing.Id))!.Endpoint);
    }

    [Fact]
    public async Task Update_WithoutNameOrEndpoint_Gets400()
    {
        var client = ClientAs("SuperAdmin");
        var existing = await SeedRegionAsync("https://93.184.216.34");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/regions/{existing.Id}", Body("", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/regions/{existing.Id}", Body("https://93.184.216.34", ""))).StatusCode);
    }

    [Fact]
    public async Task RegionHealthHttpClient_AsWiredInProgram_RefusesLoopbackAndDoesNotConnect()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var connections = 0;
        _ = Task.Run(async () =>
        {
            try { while (true) { (await tcp.AcceptTcpClientAsync()).Dispose(); Interlocked.Increment(ref connections); } }
            catch (Exception) { /* stopped */ }
        });
        try
        {
            var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("RegionHealth");
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/health"));
            await Task.Delay(200);

            Assert.Equal(0, Volatile.Read(ref connections));
        }
        finally
        {
            tcp.Stop();
        }
    }

    [Fact]
    public async Task HealthCheck_OfAStoredPrivateEndpoint_IsRefusedByTheGuard_AndRecordedAsOffline()
    {
        // A region stored before this fix (private endpoint): the check must not connect, and reports an error instead.
        var client = ClientAs("SuperAdmin");
        var region = await SeedRegionAsync("http://169.254.169.254");

        var response = await client.GetAsync($"/api/regions/{region.Id}/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("isHealthy").GetBoolean());
        Assert.Equal(-1, json.GetProperty("latencyMs").GetInt32());
        Assert.Equal(RegionStatus.Offline, (await RowAsync(region.Id))!.Status);
    }
}
