using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>Records every call so a test can prove a refusal stored, queried and deleted nothing.</summary>
public sealed class RecordingTelemetryStorage : ITelemetryStorageService
{
    private readonly object _gate = new();
    public List<TelemetryRecord> Ingested { get; } = new();
    public List<TelemetryQuery> Queries { get; } = new();
    public List<TelemetryAggregationQuery> Aggregations { get; } = new();
    public List<(string? DeviceId, Guid? TenantId)> MetricCalls { get; } = new();
    public List<Guid?> StatisticsCalls { get; } = new();
    public List<string> LatestCalls { get; } = new();
    public int CleanupCalls;

    public IReadOnlyList<TelemetryRecord> IngestedFor(string deviceId)
    {
        lock (_gate) return Ingested.Where(r => r.DeviceId == deviceId).ToList();
    }

    public Task IngestAsync(TelemetryRecord record, CancellationToken ct = default)
    {
        lock (_gate) Ingested.Add(record);
        return Task.CompletedTask;
    }

    public Task IngestBatchAsync(List<TelemetryRecord> records, CancellationToken ct = default)
    {
        lock (_gate) Ingested.AddRange(records);
        return Task.CompletedTask;
    }

    public Task<TelemetryQueryResult> QueryAsync(TelemetryQuery query, CancellationToken ct = default)
    {
        lock (_gate) Queries.Add(query);
        return Task.FromResult(new TelemetryQueryResult());
    }

    public Task<List<TelemetryAggregation>> AggregateAsync(TelemetryAggregationQuery query, CancellationToken ct = default)
    {
        lock (_gate) Aggregations.Add(query);
        return Task.FromResult(new List<TelemetryAggregation>());
    }

    public Task<List<string>> GetMetricNamesAsync(string? deviceId = null, CancellationToken ct = default)
        => GetMetricNamesAsync(deviceId, null, ct);

    public Task<List<string>> GetMetricNamesAsync(string? deviceId, Guid? tenantId, CancellationToken ct = default)
    {
        lock (_gate) MetricCalls.Add((deviceId, tenantId));
        return Task.FromResult(new List<string>());
    }

    public Task<TelemetryStatistics> GetStatisticsAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        lock (_gate) StatisticsCalls.Add(tenantId);
        return Task.FromResult(new TelemetryStatistics());
    }

    public Task<int> CleanupOldDataAsync(int retentionDays = 90, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CleanupCalls);
        return Task.FromResult(0);
    }

    public Task<List<TelemetryRecord>> GetLatestAsync(string deviceId, CancellationToken ct = default)
    {
        lock (_gate) LatestCalls.Add(deviceId);
        return Task.FromResult(new List<TelemetryRecord>());
    }

    public Task<TelemetryRecord?> GetLatestAsync(string deviceId, string metricName, CancellationToken ct = default)
        => Task.FromResult<TelemetryRecord?>(null);
}

public sealed class TelemetryAuthFixture : IDisposable
{
    public RecordingTelemetryStorage Storage { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }

    public TelemetryAuthFixture()
    {
        Factory = new IAMTestWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITelemetryStorageService>();
                services.AddSingleton<ITelemetryStorageService>(Storage);
            }));
    }

    public void Dispose() => Factory.Dispose();
}

/// <summary>
/// Task 4708: every telemetry hub method and REST endpoint checks who is calling.
/// - a device acts only for itself (publish, status, receive), never commands;
/// - users read the tenants they belong to (SuperAdmin: all); publishing, status and commands need
///   SuperAdmin/BuildingOwner/BuildingManager of the device's tenant;
/// - tenant and type of the data come from the device registry, not from the message;
/// - query/export/statistics/metrics/latest return only own-tenant data; cleanup is SuperAdmin/SystemAdmin only.
/// </summary>
public class TelemetryAuthorizationTests : IClassFixture<TelemetryAuthFixture>, IAsyncLifetime
{
    private readonly TelemetryAuthFixture _fixture;
    private readonly List<HubConnection> _connections = new();

    public TelemetryAuthorizationTests(TelemetryAuthFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
    }

    private WebApplicationFactory<Program> Factory => _fixture.Factory;
    private RecordingTelemetryStorage Storage => _fixture.Storage;

    // ----- seeding ----------------------------------------------------------------------------

    private async Task<Guid> CreateTenantAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<string> CreateDeviceAsync(
        Guid tenantId, string deviceType = "hvac", bool active = true, string[]? permissions = null)
    {
        var deviceId = $"dev-{Guid.NewGuid():N}";
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.Devices.Add(new Device
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            Name = deviceId,
            DeviceType = deviceType,
            TenantId = tenantId,
            ResourcePath = $"org:{deviceId}",
            Permissions = JsonSerializer.Serialize(permissions ?? new[] { $"org:{deviceId}:command" }),
            IsActive = active
        });
        await db.SaveChangesAsync();
        return deviceId;
    }

    /// <summary>A user with one active role row per (tenant, role name); a null tenant is a global row.</summary>
    private async Task<Guid> CreateUserAsync(params (Guid? TenantId, string Role)[] memberships)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@telemetry-authz.test",
            FirstName = "Tele",
            LastName = "Metry",
            PasswordHash = "not-used"
        };
        db.Users.Add(user);
        foreach (var (tenantId, roleName) in memberships)
        {
            var role = new Role { Id = Guid.NewGuid(), Name = roleName, Permissions = "[]" };
            db.Roles.Add(role);
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RoleId = role.Id,
                TenantId = tenantId,
                GrantedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> CreateExpiredMemberAsync(Guid tenantId, string roleName)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@telemetry-authz.test",
            FirstName = "Old",
            LastName = "Member",
            PasswordHash = "not-used"
        };
        var role = new Role { Id = Guid.NewGuid(), Name = roleName, Permissions = "[]" };
        db.Users.Add(user);
        db.Roles.Add(role);
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = role.Id,
            TenantId = tenantId,
            GrantedAt = DateTime.UtcNow.AddDays(-30),
            ExpiresAt = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static string UserToken(Guid userId, string[]? roles = null, string? tenantClaim = null) =>
        TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@telemetry-authz.test", roles ?? new[] { "User" }, tenantClaim);

    private static string SuperAdminToken() => UserToken(Guid.NewGuid(), new[] { "SuperAdmin" });

    private HttpClient ClientWith(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ----- hub plumbing -----------------------------------------------------------------------

    private sealed class Listener
    {
        public HubConnection Connection { get; init; } = null!;
        public ConcurrentQueue<(string Method, JsonElement Body)> Inbox { get; } = new();

        public async Task<JsonElement> WaitForAsync(string method, Func<JsonElement, bool>? where = null)
        {
            for (var i = 0; i < 60; i++)
            {
                var hit = Inbox.FirstOrDefault(m => m.Method == method && (where == null || where(m.Body)));
                if (hit.Method != null) return hit.Body;
                await Task.Delay(50);
            }

            throw new TimeoutException($"No {method} message arrived.");
        }

        public async Task<bool> GotNothingAsync(string method, Func<JsonElement, bool>? where = null)
        {
            await Task.Delay(400);
            return !Inbox.Any(m => m.Method == method && (where == null || where(m.Body)));
        }
    }

    private async Task<Listener> ConnectAsync(string token)
    {
        var server = Factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/telemetry"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        var listener = new Listener { Connection = connection };
        foreach (var method in new[] { "TelemetryReceived", "DeviceStatusChanged", "DeviceCommand" })
        {
            var name = method;
            connection.On<JsonElement>(name, body => listener.Inbox.Enqueue((name, body)));
        }

        await connection.StartAsync();
        _connections.Add(connection);
        return listener;
    }

    private static object Telemetry(string deviceId, string? tenantId = null, string? deviceType = null, double value = 21.5) => new
    {
        deviceId,
        deviceType = deviceType ?? "spoofed-type",
        tenantId = tenantId ?? Guid.NewGuid().ToString(),
        dataType = "temperature",
        payload = value,
        timestamp = DateTime.UtcNow
    };

    private static async Task AssertRefusedAsync(Task call)
    {
        var ex = await Assert.ThrowsAsync<HubException>(() => call);
        Assert.Contains("Unauthorized", ex.Message);
    }

    // ===== hub: subscribe to a device ========================================================

    [Fact]
    public async Task SubscribeToDevice_DeviceTokenOfThatDevice_Receives()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var listener = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(device, tenant));
        await listener.Connection.InvokeAsync("SubscribeToDevice", device);

        var manager = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "BuildingManager"))));
        await manager.Connection.InvokeAsync("PublishTelemetry", Telemetry(device));

        await listener.WaitForAsync("TelemetryReceived", b => b.GetProperty("deviceId").GetString() == device);
    }

    [Fact]
    public async Task SubscribeToDevice_DeviceTokenOfAnotherDevice_IsRefused_AndReceivesNothing()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var other = await CreateDeviceAsync(tenant);
        var listener = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(other, tenant));

        await AssertRefusedAsync(listener.Connection.InvokeAsync("SubscribeToDevice", device));

        var manager = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "BuildingManager"))));
        await manager.Connection.InvokeAsync("PublishTelemetry", Telemetry(device));
        Assert.True(await listener.GotNothingAsync("TelemetryReceived"));
    }

    [Fact]
    public async Task SubscribeToDevice_UserInTenant_Receives_UserInOtherTenant_IsRefused()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);

        var member = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "User"))));
        await member.Connection.InvokeAsync("SubscribeToDevice", device);

        var outsider = await ConnectAsync(UserToken(await CreateUserAsync((otherTenant, "BuildingOwner"))));
        await AssertRefusedAsync(outsider.Connection.InvokeAsync("SubscribeToDevice", device));

        var manager = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "BuildingManager"))));
        await manager.Connection.InvokeAsync("PublishTelemetry", Telemetry(device));

        await member.WaitForAsync("TelemetryReceived");
        Assert.True(await outsider.GotNothingAsync("TelemetryReceived"));
    }

    [Fact]
    public async Task SubscribeToDevice_SuperAdmin_Receives_UnknownDevice_IsRefused()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var admin = await ConnectAsync(SuperAdminToken());

        await admin.Connection.InvokeAsync("SubscribeToDevice", device);
        await AssertRefusedAsync(admin.Connection.InvokeAsync("SubscribeToDevice", "no-such-device"));
    }

    [Fact]
    public async Task SubscribeToDevice_ExpiredMember_ServiceAccount_AndInactiveDevice_AreRefused()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var inactive = await CreateDeviceAsync(tenant, active: false);

        var expired = await ConnectAsync(UserToken(await CreateExpiredMemberAsync(tenant, "User")));
        await AssertRefusedAsync(expired.Connection.InvokeAsync("SubscribeToDevice", device));

        var serviceAccount = await ConnectAsync(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "telemetry:read"));
        await AssertRefusedAsync(serviceAccount.Connection.InvokeAsync("SubscribeToDevice", device));

        var deviceToken = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(inactive, tenant));
        await AssertRefusedAsync(deviceToken.Connection.InvokeAsync("SubscribeToDevice", inactive));
    }

    [Fact]
    public async Task SubscribeToDevice_TenantScopedTokenOfAnotherTenant_IsRefused()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var userId = await CreateUserAsync((tenant, "User"), (otherTenant, "User"));

        var scopedElsewhere = await ConnectAsync(UserToken(userId, tenantClaim: otherTenant.ToString()));
        await AssertRefusedAsync(scopedElsewhere.Connection.InvokeAsync("SubscribeToDevice", device));

        var scopedHere = await ConnectAsync(UserToken(userId, tenantClaim: tenant.ToString()));
        await scopedHere.Connection.InvokeAsync("SubscribeToDevice", device);
    }

    // ===== hub: subscribe to a tenant / a device type ========================================

    [Fact]
    public async Task SubscribeToTenant_Member_Receives_OtherTenantUser_IsRefused()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);

        var member = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "User"))));
        await member.Connection.InvokeAsync("SubscribeToTenant", tenant.ToString());

        var outsider = await ConnectAsync(UserToken(await CreateUserAsync((otherTenant, "BuildingOwner"))));
        await AssertRefusedAsync(outsider.Connection.InvokeAsync("SubscribeToTenant", tenant.ToString()));

        var manager = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "BuildingManager"))));
        await manager.Connection.InvokeAsync("PublishTelemetry", Telemetry(device));

        await member.WaitForAsync("TelemetryReceived", b => b.GetProperty("tenantId").GetString() == tenant.ToString());
        Assert.True(await outsider.GotNothingAsync("TelemetryReceived"));
    }

    [Fact]
    public async Task SubscribeToTenant_SuperAdmin_Works_DeviceTokenAndServiceAccount_AreRefused()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);

        var admin = await ConnectAsync(SuperAdminToken());
        await admin.Connection.InvokeAsync("SubscribeToTenant", tenant.ToString());

        var deviceToken = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(device, tenant));
        await AssertRefusedAsync(deviceToken.Connection.InvokeAsync("SubscribeToTenant", tenant.ToString()));

        var serviceAccount = await ConnectAsync(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "telemetry:read"));
        await AssertRefusedAsync(serviceAccount.Connection.InvokeAsync("SubscribeToTenant", tenant.ToString()));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task SubscribeToTenant_UnparseableTenant_IsRefused(string tenantId)
    {
        var tenant = await CreateTenantAsync();
        var member = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "User"))));
        await AssertRefusedAsync(member.Connection.InvokeAsync("SubscribeToTenant", tenantId));
    }

    [Fact]
    public async Task SubscribeToDeviceType_IsPerTenant_OtherTenantsDevicesOfThatTypeAreNotDelivered()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var deviceA = await CreateDeviceAsync(tenantA, "hvac");
        var deviceB = await CreateDeviceAsync(tenantB, "hvac");

        var memberOfA = await ConnectAsync(UserToken(await CreateUserAsync((tenantA, "User"))));
        await memberOfA.Connection.InvokeAsync("SubscribeToDeviceType", "hvac");

        var managerB = await ConnectAsync(UserToken(await CreateUserAsync((tenantB, "BuildingManager"))));
        await managerB.Connection.InvokeAsync("PublishTelemetry", Telemetry(deviceB));
        var managerA = await ConnectAsync(UserToken(await CreateUserAsync((tenantA, "BuildingManager"))));
        await managerA.Connection.InvokeAsync("PublishTelemetry", Telemetry(deviceA));

        await memberOfA.WaitForAsync("TelemetryReceived", b => b.GetProperty("deviceId").GetString() == deviceA);
        Assert.True(await memberOfA.GotNothingAsync("TelemetryReceived", b => b.GetProperty("deviceId").GetString() == deviceB));
    }

    [Fact]
    public async Task SubscribeToDeviceType_UserOfSeveralTenants_AndSuperAdmin_MustNameTheTenant()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var both = await ConnectAsync(UserToken(await CreateUserAsync((tenantA, "User"), (tenantB, "User"))));
        var admin = await ConnectAsync(SuperAdminToken());

        await Assert.ThrowsAsync<HubException>(() => both.Connection.InvokeAsync("SubscribeToDeviceType", "hvac"));
        await Assert.ThrowsAsync<HubException>(() => admin.Connection.InvokeAsync("SubscribeToDeviceType", "hvac"));

        await both.Connection.InvokeAsync("SubscribeToTenantDeviceType", tenantA.ToString(), "hvac");
        await admin.Connection.InvokeAsync("SubscribeToTenantDeviceType", tenantB.ToString(), "hvac");
    }

    [Fact]
    public async Task SubscribeToTenantDeviceType_OtherTenant_DeviceToken_AndOutsider_AreRefused()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);

        var outsider = await ConnectAsync(UserToken(await CreateUserAsync((otherTenant, "User"))));
        await AssertRefusedAsync(outsider.Connection.InvokeAsync("SubscribeToTenantDeviceType", tenant.ToString(), "hvac"));

        var deviceToken = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(device, tenant));
        await AssertRefusedAsync(deviceToken.Connection.InvokeAsync("SubscribeToTenantDeviceType", tenant.ToString(), "hvac"));
        await Assert.ThrowsAsync<HubException>(() => deviceToken.Connection.InvokeAsync("SubscribeToDeviceType", "hvac"));
    }

    // ===== hub: publish telemetry ============================================================

    [Fact]
    public async Task Publish_DeviceTokenForItself_Works_AndTenantAndTypeComeFromTheRegistry()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant, "hvac");

        var watcher = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "User"))));
        await watcher.Connection.InvokeAsync("SubscribeToTenant", tenant.ToString());
        var foreignWatcher = await ConnectAsync(UserToken(await CreateUserAsync((otherTenant, "User"))));
        await foreignWatcher.Connection.InvokeAsync("SubscribeToTenant", otherTenant.ToString());

        var publisher = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(device, tenant));
        // The message claims another tenant and another type; neither is trusted.
        await publisher.Connection.InvokeAsync("PublishTelemetry", Telemetry(device, otherTenant.ToString(), "spoofed-type"));

        var stored = Assert.Single(Storage.IngestedFor(device));
        Assert.Equal(tenant, stored.TenantId);
        Assert.Equal("hvac", stored.DeviceType);

        var received = await watcher.WaitForAsync("TelemetryReceived");
        Assert.Equal(tenant.ToString(), received.GetProperty("tenantId").GetString());
        Assert.Equal("hvac", received.GetProperty("deviceType").GetString());
        Assert.True(await foreignWatcher.GotNothingAsync("TelemetryReceived"));
    }

    [Fact]
    public async Task Publish_DeviceTokenForAnotherDevice_IsRefused_NothingStoredOrBroadcast()
    {
        var tenant = await CreateTenantAsync();
        var victim = await CreateDeviceAsync(tenant);
        var attacker = await CreateDeviceAsync(tenant);

        var watcher = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "User"))));
        await watcher.Connection.InvokeAsync("SubscribeToDevice", victim);

        var publisher = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(attacker, tenant));
        await AssertRefusedAsync(publisher.Connection.InvokeAsync("PublishTelemetry", Telemetry(victim, tenant.ToString())));

        Assert.Empty(Storage.IngestedFor(victim));
        Assert.True(await watcher.GotNothingAsync("TelemetryReceived"));
    }

    [Fact]
    public async Task Publish_DeviceTokenWithAnotherTenantClaim_AndUnknownDevice_AreRefused()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);

        var wrongTenantToken = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(device, otherTenant));
        await AssertRefusedAsync(wrongTenantToken.Connection.InvokeAsync("PublishTelemetry", Telemetry(device)));

        var ghost = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken("ghost-device", tenant));
        await AssertRefusedAsync(ghost.Connection.InvokeAsync("PublishTelemetry", Telemetry("ghost-device", tenant.ToString())));
        Assert.Empty(Storage.IngestedFor("ghost-device"));
        Assert.Empty(Storage.IngestedFor(device));
    }

    [Fact]
    public async Task Publish_UserRoles_ManagerOwnerAndSuperAdminWork_PlainMemberAndOutsiderDoNot()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);

        foreach (var token in new[]
                 {
                     UserToken(await CreateUserAsync((tenant, "BuildingManager"))),
                     UserToken(await CreateUserAsync((tenant, "BuildingOwner"))),
                     SuperAdminToken()
                 })
        {
            var allowed = await ConnectAsync(token);
            await allowed.Connection.InvokeAsync("PublishTelemetry", Telemetry(device));
        }

        Assert.Equal(3, Storage.IngestedFor(device).Count);

        foreach (var token in new[]
                 {
                     UserToken(await CreateUserAsync((tenant, "User"))),
                     UserToken(await CreateUserAsync((tenant, "TenantAdmin"))),
                     UserToken(await CreateUserAsync((otherTenant, "BuildingManager"))),
                     UserToken(await CreateExpiredMemberAsync(tenant, "BuildingManager")),
                     UserToken(await CreateUserAsync((null, "BuildingManager"))),
                     UserToken(Guid.NewGuid(), new[] { "BuildingManager" }),
                     TestAuthenticationHelper.GenerateServiceAccountToken("svc", "telemetry:write")
                 })
        {
            var refused = await ConnectAsync(token);
            await AssertRefusedAsync(refused.Connection.InvokeAsync("PublishTelemetry", Telemetry(device)));
        }

        Assert.Equal(3, Storage.IngestedFor(device).Count);
    }

    // ===== hub: device status ================================================================

    private async Task<(bool Online, string? Ip)> RegistryStatusAsync(string deviceId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var device = await db.Devices.AsNoTracking().FirstAsync(d => d.DeviceId == deviceId);
        return (device.IsOnline, device.LastIpAddress);
    }

    [Fact]
    public async Task ReportStatus_DeviceForItself_Works_AndBroadcastsToTheRegistryTenant()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var watcher = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "User"))));
        await watcher.Connection.InvokeAsync("SubscribeToTenant", tenant.ToString());
        var foreignWatcher = await ConnectAsync(UserToken(await CreateUserAsync((otherTenant, "User"))));
        await foreignWatcher.Connection.InvokeAsync("SubscribeToTenant", otherTenant.ToString());

        var reporter = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(device, tenant));
        await reporter.Connection.InvokeAsync("ReportDeviceStatus",
            new { deviceId = device, tenantId = otherTenant.ToString(), isOnline = true, ipAddress = "10.1.2.3" });

        Assert.Equal((true, "10.1.2.3"), await RegistryStatusAsync(device));
        await watcher.WaitForAsync("DeviceStatusChanged", b => b.GetProperty("deviceId").GetString() == device);
        Assert.True(await foreignWatcher.GotNothingAsync("DeviceStatusChanged"));
    }

    [Fact]
    public async Task ReportStatus_SpoofingAnotherDevice_IsRefused_RegistryUntouched()
    {
        var tenant = await CreateTenantAsync();
        var victim = await CreateDeviceAsync(tenant);
        var attacker = await CreateDeviceAsync(tenant);

        var reporter = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(attacker, tenant));
        await AssertRefusedAsync(reporter.Connection.InvokeAsync("ReportDeviceStatus",
            new { deviceId = victim, tenantId = tenant.ToString(), isOnline = true, ipAddress = "6.6.6.6" }));

        Assert.Equal((false, null), await RegistryStatusAsync(victim));
    }

    [Fact]
    public async Task ReportStatus_Users_ManagerAndSuperAdminWork_PlainMemberAndOutsiderDoNot()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);

        var member = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "User"))));
        await AssertRefusedAsync(member.Connection.InvokeAsync("ReportDeviceStatus",
            new { deviceId = device, tenantId = tenant.ToString(), isOnline = true, ipAddress = "1.1.1.1" }));
        var outsider = await ConnectAsync(UserToken(await CreateUserAsync((otherTenant, "BuildingManager"))));
        await AssertRefusedAsync(outsider.Connection.InvokeAsync("ReportDeviceStatus",
            new { deviceId = device, tenantId = otherTenant.ToString(), isOnline = true, ipAddress = "1.1.1.1" }));
        Assert.Equal((false, null), await RegistryStatusAsync(device));

        var manager = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "BuildingManager"))));
        await manager.Connection.InvokeAsync("ReportDeviceStatus",
            new { deviceId = device, tenantId = tenant.ToString(), isOnline = true, ipAddress = "2.2.2.2" });
        Assert.Equal((true, "2.2.2.2"), await RegistryStatusAsync(device));

        var admin = await ConnectAsync(SuperAdminToken());
        await admin.Connection.InvokeAsync("ReportDeviceStatus",
            new { deviceId = device, tenantId = tenant.ToString(), isOnline = false, ipAddress = "3.3.3.3" });
        Assert.Equal((false, "3.3.3.3"), await RegistryStatusAsync(device));
    }

    // ===== hub: commands =====================================================================

    private static object Command(string deviceId, string resource) => new
    {
        deviceId,
        resource,
        commandType = "setpoint",
        payload = 19
    };

    [Fact]
    public async Task Command_ManagerOfTheDevicesTenant_IsForwardedToTheDevice()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var deviceSide = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(device, tenant));
        await deviceSide.Connection.InvokeAsync("SubscribeToDevice", device);

        var managerId = await CreateUserAsync((tenant, "BuildingManager"));
        var manager = await ConnectAsync(UserToken(managerId));
        await manager.Connection.InvokeAsync("SendDeviceCommand", Command(device, $"org:{device}"));

        var received = await deviceSide.WaitForAsync("DeviceCommand");
        Assert.Equal(managerId.ToString(), received.GetProperty("issuedBy").GetString());
        Assert.Equal("setpoint", received.GetProperty("commandType").GetString());
    }

    [Fact]
    public async Task Command_SuperAdmin_Works_WhenTheDeviceItselfAllowsTheCommand()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var admin = await ConnectAsync(SuperAdminToken());

        await admin.Connection.InvokeAsync("SendDeviceCommand", Command(device, $"org:{device}"));
    }

    [Fact]
    public async Task Command_KeepsTheDevicePermissionCheck_OnTopOfTheRoleCheck()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant, permissions: new[] { "org:other:command" });
        var manager = await ConnectAsync(UserToken(await CreateUserAsync((tenant, "BuildingManager"))));

        await AssertRefusedAsync(manager.Connection.InvokeAsync("SendDeviceCommand", Command(device, $"org:{device}")));
    }

    [Fact]
    public async Task Command_DeviceTokens_PlainMembers_OutsidersAndServiceAccounts_AreRefused_NothingForwarded()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var peer = await CreateDeviceAsync(tenant);
        var deviceSide = await ConnectAsync(TestAuthenticationHelper.GenerateDeviceToken(device, tenant));
        await deviceSide.Connection.InvokeAsync("SubscribeToDevice", device);

        foreach (var token in new[]
                 {
                     TestAuthenticationHelper.GenerateDeviceToken(device, tenant),
                     TestAuthenticationHelper.GenerateDeviceToken(peer, tenant),
                     UserToken(await CreateUserAsync((tenant, "User"))),
                     UserToken(await CreateUserAsync((otherTenant, "BuildingOwner"))),
                     TestAuthenticationHelper.GenerateServiceAccountToken("svc", "devices:command")
                 })
        {
            var caller = await ConnectAsync(token);
            await AssertRefusedAsync(caller.Connection.InvokeAsync("SendDeviceCommand", Command(device, $"org:{device}")));
        }

        Assert.True(await deviceSide.GotNothingAsync("DeviceCommand"));
    }

    // ===== REST: ingest ======================================================================

    private static object Ingest(string deviceId, Guid? tenantId = null, string? deviceType = null) => new
    {
        deviceId,
        metricName = "temperature",
        numericValue = 21.5,
        tenantId,
        deviceType
    };

    [Fact]
    public async Task RestIngest_DeviceTokenForItself_Works_AndTenantAndTypeComeFromTheRegistry()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant, "sensor");

        var response = await ClientWith(TestAuthenticationHelper.GenerateDeviceToken(device, tenant))
            .PostAsJsonAsync("/api/telemetry/ingest", Ingest(device, otherTenant, "spoofed-type"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(Storage.IngestedFor(device));
        Assert.Equal(tenant, stored.TenantId);
        Assert.Equal("sensor", stored.DeviceType);
    }

    [Fact]
    public async Task RestIngest_AnotherDevice_UnknownDevice_PlainMember_Outsider_ServiceAccount_AreRefused()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var peer = await CreateDeviceAsync(tenant);

        var cases = new (string Token, string DeviceId)[]
        {
            (TestAuthenticationHelper.GenerateDeviceToken(peer, tenant), device),
            (TestAuthenticationHelper.GenerateDeviceToken("ghost-device", tenant), "ghost-device"),
            (SuperAdminToken(), "ghost-device"),
            (UserToken(await CreateUserAsync((tenant, "User"))), device),
            (UserToken(await CreateUserAsync((otherTenant, "BuildingManager"))), device),
            (TestAuthenticationHelper.GenerateServiceAccountToken("svc", "telemetry:write"), device)
        };

        foreach (var (token, deviceId) in cases)
        {
            var response = await ClientWith(token).PostAsJsonAsync("/api/telemetry/ingest", Ingest(deviceId, tenant));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Empty(Storage.IngestedFor(device));
        Assert.Empty(Storage.IngestedFor("ghost-device"));
    }

    [Fact]
    public async Task RestIngest_ManagerOwnerAndSuperAdmin_Work()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);

        foreach (var token in new[]
                 {
                     UserToken(await CreateUserAsync((tenant, "BuildingManager"))),
                     UserToken(await CreateUserAsync((tenant, "BuildingOwner"))),
                     SuperAdminToken()
                 })
        {
            var response = await ClientWith(token).PostAsJsonAsync("/api/telemetry/ingest", Ingest(device));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, Storage.IngestedFor(device).Count);
    }

    [Fact]
    public async Task RestIngestBatch_DeviceTokenCoversOnlyItself_OneForeignRecordRejectsTheWholeBatch()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var peer = await CreateDeviceAsync(tenant);
        var client = ClientWith(TestAuthenticationHelper.GenerateDeviceToken(device, tenant));

        var mixed = await client.PostAsJsonAsync("/api/telemetry/ingest/batch",
            new { records = new[] { Ingest(device), Ingest(peer) } });
        Assert.Equal(HttpStatusCode.Forbidden, mixed.StatusCode);
        Assert.Empty(Storage.IngestedFor(device));
        Assert.Empty(Storage.IngestedFor(peer));

        var own = await client.PostAsJsonAsync("/api/telemetry/ingest/batch",
            new { records = new[] { Ingest(device), Ingest(device) } });
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(2, Storage.IngestedFor(device).Count);
    }

    [Fact]
    public async Task RestIngestBatch_Users_ManagerWorks_MemberAndOutsiderDoNot_TenantComesFromTheRegistry()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant, "meter");

        var member = ClientWith(UserToken(await CreateUserAsync((tenant, "User"))));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/api/telemetry/ingest/batch", new { records = new[] { Ingest(device) } })).StatusCode);
        var outsider = ClientWith(UserToken(await CreateUserAsync((otherTenant, "BuildingOwner"))));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync("/api/telemetry/ingest/batch", new { records = new[] { Ingest(device) } })).StatusCode);
        Assert.Empty(Storage.IngestedFor(device));

        var manager = ClientWith(UserToken(await CreateUserAsync((tenant, "BuildingManager"))));
        var ok = await manager.PostAsJsonAsync("/api/telemetry/ingest/batch",
            new { records = new[] { Ingest(device, otherTenant, "spoofed-type") } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var stored = Assert.Single(Storage.IngestedFor(device));
        Assert.Equal(tenant, stored.TenantId);
        Assert.Equal("meter", stored.DeviceType);
    }

    // ===== REST: reads =======================================================================

    [Fact]
    public async Task Query_IsPinnedToTheCallersOwnTenant_AndAnotherTenantIsRefused()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var client = ClientWith(UserToken(await CreateUserAsync((tenant, "User"))));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/telemetry/query")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/telemetry/query?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/telemetry/query?tenantId={otherTenant}")).StatusCode);
        // A device filter does not widen the scope.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/telemetry/query?deviceId=someone-elses-device")).StatusCode);

        var mine = Storage.Queries.Where(q => q.TenantId == tenant).ToList();
        Assert.Equal(3, mine.Count);
        Assert.DoesNotContain(Storage.Queries, q => q.TenantId == otherTenant || q.TenantId == null && q.DeviceId == "someone-elses-device");
    }

    [Fact]
    public async Task Query_UserOfSeveralTenants_MustNameOne_SuperAdminMayAskForAll()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var both = ClientWith(UserToken(await CreateUserAsync((tenantA, "User"), (tenantB, "User"))));
        var admin = ClientWith(SuperAdminToken());

        Assert.Equal(HttpStatusCode.BadRequest, (await both.GetAsync("/api/telemetry/query")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await both.GetAsync($"/api/telemetry/query?tenantId={tenantB}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/telemetry/query?tenantId={tenantA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/telemetry/query")).StatusCode);

        Assert.Contains(Storage.Queries, q => q.TenantId == tenantB);
        Assert.Contains(Storage.Queries, q => q.TenantId == tenantA);
    }

    [Fact]
    public async Task Reads_DeviceTokensServiceAccountsOutsidersAndExpiredMembers_AreRefused()
    {
        var tenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);

        var tokens = new[]
        {
            TestAuthenticationHelper.GenerateDeviceToken(device, tenant),
            TestAuthenticationHelper.GenerateServiceAccountToken("svc", "telemetry:read"),
            UserToken(await CreateUserAsync()),
            UserToken(await CreateExpiredMemberAsync(tenant, "User"))
        };
        var urls = new[]
        {
            $"/api/telemetry/query?tenantId={tenant}",
            $"/api/telemetry/export?tenantId={tenant}",
            $"/api/telemetry/aggregate?metricName=temperature&tenantId={tenant}",
            $"/api/telemetry/statistics?tenantId={tenant}",
            "/api/telemetry/metrics"
        };

        foreach (var token in tokens)
        foreach (var url in urls)
        {
            var response = await ClientWith(token).GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.DoesNotContain(Storage.Queries, q => q.TenantId == tenant);
        Assert.DoesNotContain(Storage.Aggregations, q => q.TenantId == tenant);
        Assert.DoesNotContain(Storage.StatisticsCalls, t => t == tenant);
    }

    [Fact]
    public async Task ExportAggregateAndStatistics_ArePinnedToTheCallersTenant()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var client = ClientWith(UserToken(await CreateUserAsync((tenant, "User"))));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/telemetry/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/telemetry/aggregate?metricName=temperature")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/telemetry/statistics")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/telemetry/export?tenantId={otherTenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/telemetry/aggregate?metricName=temperature&tenantId={otherTenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/telemetry/statistics?tenantId={otherTenant}")).StatusCode);

        Assert.Contains(Storage.Queries, q => q.TenantId == tenant && q.OrderBy == "timestamp_asc");
        Assert.Contains(Storage.Aggregations, q => q.TenantId == tenant);
        Assert.Contains(Storage.StatisticsCalls, t => t == tenant);
        Assert.DoesNotContain(Storage.Queries, q => q.TenantId == otherTenant);
        Assert.DoesNotContain(Storage.Aggregations, q => q.TenantId == otherTenant);
        Assert.DoesNotContain(Storage.StatisticsCalls, t => t == otherTenant);
    }

    [Fact]
    public async Task Latest_AndMetricsOfADevice_FollowTheDevicesTenant()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var device = await CreateDeviceAsync(tenant);
        var member = ClientWith(UserToken(await CreateUserAsync((tenant, "User"))));
        var outsider = ClientWith(UserToken(await CreateUserAsync((otherTenant, "BuildingOwner"))));
        var own = ClientWith(TestAuthenticationHelper.GenerateDeviceToken(device, tenant));
        var peer = ClientWith(TestAuthenticationHelper.GenerateDeviceToken(await CreateDeviceAsync(tenant), tenant));

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/telemetry/latest/{device}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await own.GetAsync($"/api/telemetry/latest/{device}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientWith(SuperAdminToken()).GetAsync($"/api/telemetry/latest/{device}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/telemetry/latest/{device}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await peer.GetAsync($"/api/telemetry/latest/{device}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/telemetry/latest/no-such-device")).StatusCode);
        Assert.Equal(3, Storage.LatestCalls.Count(d => d == device));

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/telemetry/metrics?deviceId={device}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/telemetry/metrics?deviceId={device}")).StatusCode);
        Assert.Contains(Storage.MetricCalls, c => c.DeviceId == device && c.TenantId == tenant);
    }

    [Fact]
    public async Task Metrics_WithoutADevice_IsPinnedToTheCallersTenant()
    {
        var tenant = await CreateTenantAsync();
        var member = ClientWith(UserToken(await CreateUserAsync((tenant, "User"))));

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/telemetry/metrics")).StatusCode);

        Assert.Contains(Storage.MetricCalls, c => c.DeviceId == null && c.TenantId == tenant);
    }

    // ===== REST: cleanup =====================================================================

    [Fact]
    public async Task Cleanup_IsSuperAdminOrSystemAdminOnly()
    {
        var tenant = await CreateTenantAsync();
        var before = Storage.CleanupCalls;

        foreach (var token in new[]
                 {
                     UserToken(await CreateUserAsync((tenant, "BuildingOwner")), new[] { "BuildingOwner" }),
                     UserToken(await CreateUserAsync((tenant, "TenantAdmin")), new[] { "TenantAdmin" }),
                     UserToken(Guid.NewGuid()),
                     TestAuthenticationHelper.GenerateDeviceToken("dev-x", tenant),
                     TestAuthenticationHelper.GenerateServiceAccountToken("svc", "telemetry:cleanup")
                 })
        {
            var response = await ClientWith(token).PostAsJsonAsync("/api/telemetry/cleanup", new { retentionDays = 30 });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(before, Storage.CleanupCalls);

        Assert.Equal(HttpStatusCode.OK,
            (await ClientWith(SuperAdminToken()).PostAsJsonAsync("/api/telemetry/cleanup", new { retentionDays = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await ClientWith(UserToken(Guid.NewGuid(), new[] { "SystemAdmin" })).PostAsJsonAsync("/api/telemetry/cleanup", new { retentionDays = 30 })).StatusCode);
        Assert.Equal(before + 2, Storage.CleanupCalls);
    }

    [Fact]
    public async Task EveryEndpoint_RejectsAnUnauthenticatedCaller()
    {
        var anonymous = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/telemetry/query")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/telemetry/ingest", Ingest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/telemetry/cleanup", new { retentionDays = 30 })).StatusCode);
    }
}
