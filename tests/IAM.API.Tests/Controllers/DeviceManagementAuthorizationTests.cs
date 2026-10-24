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
/// Task 4726: every /api/devices action is checked against the caller. Reading needs membership of the device's
/// tenant (any non-expired UserRoles row), changing needs SuperAdmin or a building-management role in that tenant,
/// lists show only what the caller may see, device and service-account tokens are refused, and a tenant_id claim
/// pins a token to its tenant. API-key callers are covered by <see cref="DeviceManagementApiKeyTests"/>.
/// </summary>
public class DeviceManagementAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IAMTestWebApplicationFactory _factory;

    public DeviceManagementAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------

    private async Task<Guid> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@device-authz.test", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task GrantRoleAsync(Guid userId, string roleName, Guid? tenantId, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role == null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName, TenantId = RootTenantId };
            db.Roles.Add(role);
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id,
            TenantId = tenantId,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid Id, string DeviceId)> SeedDeviceAsync(Guid tenantId, bool isActive = true, string deviceType = "sensor")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceId = $"dev-{Guid.NewGuid():N}",
            Name = "seeded",
            DeviceType = deviceType,
            AuthenticationMethod = "hmac",
            TenantId = tenantId,
            ResourcePath = "acme:hq:floor-1:sensor:1",
            Permissions = "[\"acme:hq:floor-1:sensor:1:telemetry:write\"]",
            IsActive = isActive
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return (device.Id, device.DeviceId);
    }

    private async Task<Device?> DeviceRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
    }

    private async Task<Device?> DeviceRowAsync(string deviceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId);
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@device-authz.test", roles, tenantClaim));

    /// <summary>A user holding the given role in the given tenant (a null tenant = an unscoped, "global" row).</summary>
    private async Task<(Guid UserId, HttpClient Client)> UserWithRoleAsync(
        string roleName, Guid? tenantId, DateTime? expiresAt = null, string? tenantClaim = null)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, roleName, tenantId, expiresAt);
        return (userId, ClientAs(userId, new[] { roleName }, tenantClaim));
    }

    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });

    private static object RegisterBody(Guid tenantId, string? deviceId = null, Guid? provisionedByUserId = null) => new
    {
        deviceId = deviceId ?? $"new-{Guid.NewGuid():N}",
        name = "New device",
        deviceType = "sensor",
        authenticationMethod = "hmac",
        tenantId,
        resourcePath = "acme:hq:floor-2:sensor:9",
        permissions = new[] { "acme:hq:floor-2:sensor:9:telemetry:write" },
        provisionedByUserId
    };

    private static async Task<List<string>> DeviceIdsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.EnumerateArray().Select(e => e.GetProperty("deviceId").GetString()!).ToList();
    }

    /// <summary>One call per endpoint, all aimed at the given device/tenant; order matches <see cref="EndpointNames"/>.</summary>
    private static async Task<HttpStatusCode[]> AllEndpointsAsync(HttpClient c, Guid tenantId, Guid deviceInternalId, string deviceId) => new[]
    {
        (await c.PostAsJsonAsync("/api/devices", RegisterBody(tenantId))).StatusCode,
        (await c.GetAsync("/api/devices")).StatusCode,
        (await c.GetAsync($"/api/devices/{deviceInternalId}")).StatusCode,
        (await c.GetAsync($"/api/devices/by-device-id/{deviceId}")).StatusCode,
        (await c.GetAsync($"/api/devices/by-tenant/{tenantId}")).StatusCode,
        (await c.GetAsync("/api/devices/by-type/sensor")).StatusCode,
        (await c.PutAsJsonAsync($"/api/devices/{deviceInternalId}", new { name = "renamed", isActive = true })).StatusCode,
        (await c.PostAsync($"/api/devices/{deviceInternalId}/deactivate", null)).StatusCode,
        (await c.GetAsync("/api/devices/statistics")).StatusCode,
    };

    private static readonly string[] EndpointNames =
    {
        "register", "list", "get", "by-device-id", "by-tenant", "by-type", "update", "deactivate", "statistics"
    };

    // ----- callers that are refused outright ---------------------------------------------------

    [Fact]
    public async Task Anonymous_Gets401()
    {
        var (id, deviceId) = await SeedDeviceAsync(await CreateTenantAsync());

        var statuses = await AllEndpointsAsync(_factory.CreateClient(), Guid.NewGuid(), id, deviceId);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }

    [Fact]
    public async Task DeviceToken_Gets403_OnEveryEndpoint_AndNothingChanges()
    {
        var tenant = await CreateTenantAsync();
        var (id, deviceId) = await SeedDeviceAsync(tenant);
        // A device token of the very device in the very tenant: still not a tenant member or administrator.
        var client = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(id, deviceId, tenant));

        var statuses = await AllEndpointsAsync(client, tenant, id, deviceId);

        for (var i = 0; i < statuses.Length; i++)
            Assert.True(statuses[i] == HttpStatusCode.Forbidden, $"{EndpointNames[i]} returned {statuses[i]}");
        var row = await DeviceRowAsync(id);
        Assert.True(row!.IsActive);
        Assert.Equal("seeded", row.Name);
    }

    [Fact]
    public async Task ServiceAccountToken_Gets403_OnEveryEndpoint_EvenWithDevicePermissions()
    {
        var tenant = await CreateTenantAsync();
        var (id, deviceId) = await SeedDeviceAsync(tenant);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "devices:read", "devices:write"));

        var statuses = await AllEndpointsAsync(client, tenant, id, deviceId);

        for (var i = 0; i < statuses.Length; i++)
            Assert.True(statuses[i] == HttpStatusCode.Forbidden, $"{EndpointNames[i]} returned {statuses[i]}");
        Assert.True((await DeviceRowAsync(id))!.IsActive);
    }

    // ----- a user who belongs to nothing -------------------------------------------------------

    [Fact]
    public async Task UserWithoutMembership_CannotChangeAnything_AndSeesNothing()
    {
        var tenant = await CreateTenantAsync();
        var (id, deviceId) = await SeedDeviceAsync(tenant);
        var client = ClientAs(await CreateUserAsync(), new[] { "User" });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/devices", RegisterBody(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/devices/{id}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/devices/{id}/deactivate", null)).StatusCode);
        // No existence oracle for ids either: an unknown id answers the same 403 as a real one.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/devices/{Guid.NewGuid()}/deactivate", null)).StatusCode);

        Assert.Empty(await DeviceIdsAsync(await client.GetAsync("/api/devices")));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/by-device-id/{deviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/devices/by-tenant/{tenant}")).StatusCode);
        Assert.Empty(await DeviceIdsAsync(await client.GetAsync("/api/devices/by-type/sensor")));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/devices/by-type/sensor?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/devices/statistics?tenantId={tenant}")).StatusCode);
        var stats = await (await client.GetAsync("/api/devices/statistics")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, stats.GetProperty("totalDevices").GetInt32());

        var row = await DeviceRowAsync(id);
        Assert.True(row!.IsActive);
        Assert.Equal("seeded", row.Name);
    }

    // ----- member: may read the tenant, may not change it --------------------------------------

    [Fact]
    public async Task Member_CanReadOwnTenant_ButCannotRegisterUpdateOrDeactivate()
    {
        var tenant = await CreateTenantAsync();
        var (id, deviceId) = await SeedDeviceAsync(tenant);
        var (_, client) = await UserWithRoleAsync("User", tenant);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/devices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/devices/by-device-id/{deviceId}")).StatusCode);
        Assert.Contains(deviceId, await DeviceIdsAsync(await client.GetAsync($"/api/devices/by-tenant/{tenant}")));
        Assert.Contains(deviceId, await DeviceIdsAsync(await client.GetAsync($"/api/devices/by-type/sensor?tenantId={tenant}")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/devices/statistics?tenantId={tenant}")).StatusCode);

        var register = RegisterBody(tenant, "member-attempt");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/devices", register)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/devices/{id}", new { name = "x", isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/devices/{id}/deactivate", null)).StatusCode);

        Assert.Null(await DeviceRowAsync("member-attempt"));
        var row = await DeviceRowAsync(id);
        Assert.True(row!.IsActive);
        Assert.Equal("seeded", row.Name);
    }

    [Fact]
    public async Task Member_ListsShowOnlyTheirOwnTenants()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var c = await CreateTenantAsync();
        var (_, devA) = await SeedDeviceAsync(a);
        var (_, devB) = await SeedDeviceAsync(b);
        var (idC, devC) = await SeedDeviceAsync(c);
        var (userId, _) = await UserWithRoleAsync("User", a);
        await GrantRoleAsync(userId, "User", b);
        var client = ClientAs(userId, new[] { "User" });

        var all = await DeviceIdsAsync(await client.GetAsync("/api/devices"));
        var byType = await DeviceIdsAsync(await client.GetAsync("/api/devices/by-type/sensor"));

        Assert.Contains(devA, all);
        Assert.Contains(devB, all);
        Assert.DoesNotContain(devC, all);
        Assert.Contains(devA, byType);
        Assert.Contains(devB, byType);
        Assert.DoesNotContain(devC, byType);
        var stats = await (await client.GetAsync("/api/devices/statistics")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, stats.GetProperty("totalDevices").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/{idC}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/by-device-id/{devC}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/devices/by-tenant/{c}")).StatusCode);
    }

    // ----- manager: may change the own tenant only ---------------------------------------------

    [Theory]
    [InlineData("BuildingManager")]
    [InlineData("BuildingOwner")]
    [InlineData("TenantAdmin")]
    public async Task Manager_RegistersInOwnTenant_GetsSecret_AndProvisionedByIsTheCaller(string role)
    {
        var tenant = await CreateTenantAsync();
        var (managerId, client) = await UserWithRoleAsync(role, tenant);
        var forged = Guid.NewGuid();
        var body = RegisterBody(tenant, provisionedByUserId: forged);

        var response = await client.PostAsJsonAsync("/api/devices", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("sharedSecret").GetString()));
        var row = await DeviceRowAsync(json.GetProperty("deviceId").GetString()!);
        Assert.Equal(tenant, row!.TenantId);
        Assert.Equal(managerId, row.ProvisionedByUserId);
        Assert.NotEqual(forged, row.ProvisionedByUserId);
    }

    [Fact]
    public async Task Manager_CannotRegisterInAnotherTenant_NothingStored()
    {
        var own = await CreateTenantAsync();
        var victim = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("BuildingManager", own);
        var deviceId = $"intruder-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/devices", RegisterBody(victim, deviceId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await response.Content.ReadAsStringAsync()).Contains("sharedSecret", StringComparison.OrdinalIgnoreCase));
        Assert.Null(await DeviceRowAsync(deviceId));
    }

    [Fact]
    public async Task Manager_RegisteringInAnotherTenant_DoesNotRevealWhichDeviceIdsExist()
    {
        var own = await CreateTenantAsync();
        var victim = await CreateTenantAsync();
        var (_, existingDeviceId) = await SeedDeviceAsync(victim);
        var (_, client) = await UserWithRoleAsync("BuildingManager", own);

        var taken = await client.PostAsJsonAsync("/api/devices", RegisterBody(victim, existingDeviceId));
        var free = await client.PostAsJsonAsync("/api/devices", RegisterBody(victim, $"free-{Guid.NewGuid():N}"));

        // The tenant check runs before the "Device ID already registered" check, so both answer the same.
        Assert.Equal(HttpStatusCode.Forbidden, taken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, free.StatusCode);
    }

    [Fact]
    public async Task Manager_CanUpdateAndDeactivateOwnDevice_AndReactivateViaPut()
    {
        var tenant = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(tenant);
        var (_, client) = await UserWithRoleAsync("BuildingManager", tenant);

        var update = await client.PutAsJsonAsync($"/api/devices/{id}", new
        {
            name = "renamed",
            resourcePath = "acme:hq:floor-9:sensor:2",
            permissions = new[] { "acme:hq:floor-9:sensor:2:telemetry:write" }
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var renamed = await DeviceRowAsync(id);
        Assert.Equal("renamed", renamed!.Name);
        Assert.Equal("acme:hq:floor-9:sensor:2", renamed.ResourcePath);

        var deactivate = await client.PostAsync($"/api/devices/{id}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.False((await DeviceRowAsync(id))!.IsActive);

        var reactivate = await client.PutAsJsonAsync($"/api/devices/{id}", new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
        Assert.True((await DeviceRowAsync(id))!.IsActive);
    }

    [Fact]
    public async Task Manager_CannotChangeADeviceOfAnotherTenant_NothingChanges()
    {
        var own = await CreateTenantAsync();
        var victim = await CreateTenantAsync();
        var (activeId, _) = await SeedDeviceAsync(victim);
        var (inactiveId, _) = await SeedDeviceAsync(victim, isActive: false);
        var (_, client) = await UserWithRoleAsync("BuildingManager", own);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/devices/{activeId}",
            new { name = "pwned", resourcePath = "evil:path", permissions = new[] { "*" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/devices/{inactiveId}", new { isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/devices/{activeId}/deactivate", null)).StatusCode);

        var active = await DeviceRowAsync(activeId);
        Assert.Equal("seeded", active!.Name);
        Assert.Equal("acme:hq:floor-1:sensor:1", active.ResourcePath);
        Assert.Equal("[\"acme:hq:floor-1:sensor:1:telemetry:write\"]", active.Permissions);
        Assert.True(active.IsActive);
        Assert.False((await DeviceRowAsync(inactiveId))!.IsActive);
    }

    [Fact]
    public async Task Manager_ReadsOfAnotherTenant_Are404ForDevicesAnd403ForTheTenantInTheUrl()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var (otherId, otherDeviceId) = await SeedDeviceAsync(other);
        var (_, ownDeviceId) = await SeedDeviceAsync(own);
        var (_, client) = await UserWithRoleAsync("BuildingManager", own);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/{otherId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/by-device-id/{otherDeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/devices/by-tenant/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/devices/by-type/sensor?tenantId={other}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/devices/statistics?tenantId={other}")).StatusCode);

        var all = await DeviceIdsAsync(await client.GetAsync("/api/devices"));
        Assert.Contains(ownDeviceId, all);
        Assert.DoesNotContain(otherDeviceId, all);
    }

    [Fact]
    public async Task Manager_PutBodyCannotMoveADeviceToAnotherTenant()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(own);
        var (_, client) = await UserWithRoleAsync("BuildingManager", own);

        var response = await client.PutAsJsonAsync($"/api/devices/{id}", new { name = "moved", tenantId = other });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(own, (await DeviceRowAsync(id))!.TenantId);
    }

    [Fact]
    public async Task Manager_UpdatingAnUnknownDevice_Gets404()
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("BuildingManager", tenant);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/devices/{Guid.NewGuid()}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/devices/{Guid.NewGuid()}/deactivate", null)).StatusCode);
    }

    // ----- manager of ANOTHER tenant, the same person who manages B acting on A ------------------

    [Fact]
    public async Task ManagerOfAnotherTenant_Gets403Or404_OnEveryEndpoint_AndNothingChanges()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var (id, deviceId) = await SeedDeviceAsync(tenantA);
        var (_, managerOfB) = await UserWithRoleAsync("BuildingManager", tenantB);

        var statuses = await AllEndpointsAsync(managerOfB, tenantA, id, deviceId);

        var expected = new[]
        {
            HttpStatusCode.Forbidden,  // register in A
            HttpStatusCode.OK,         // list: filtered to B, so A's device is simply absent
            HttpStatusCode.NotFound,   // get
            HttpStatusCode.NotFound,   // by-device-id
            HttpStatusCode.Forbidden,  // by-tenant A
            HttpStatusCode.OK,         // by-type: filtered to B
            HttpStatusCode.Forbidden,  // update
            HttpStatusCode.Forbidden,  // deactivate
            HttpStatusCode.OK,         // statistics: filtered to B
        };
        for (var i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == statuses[i], $"{EndpointNames[i]} returned {statuses[i]}, expected {expected[i]}");

        Assert.DoesNotContain(deviceId, await DeviceIdsAsync(await managerOfB.GetAsync("/api/devices")));
        Assert.DoesNotContain(deviceId, await DeviceIdsAsync(await managerOfB.GetAsync("/api/devices/by-type/sensor")));
        var row = await DeviceRowAsync(id);
        Assert.True(row!.IsActive);
        Assert.Equal("seeded", row.Name);
    }

    // ----- SuperAdmin keeps full access ------------------------------------------------------------

    [Fact]
    public async Task SuperAdmin_CanDoEverything_InAnyTenant()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var (idA, devA) = await SeedDeviceAsync(a);
        var (idB, devB) = await SeedDeviceAsync(b);
        var client = SuperAdmin();

        var register = await client.PostAsJsonAsync("/api/devices", RegisterBody(b));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.False(string.IsNullOrEmpty((await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedSecret").GetString()));

        var all = await DeviceIdsAsync(await client.GetAsync("/api/devices"));
        Assert.Contains(devA, all);
        Assert.Contains(devB, all);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/devices/{idA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/devices/by-device-id/{devB}")).StatusCode);
        Assert.Contains(devA, await DeviceIdsAsync(await client.GetAsync($"/api/devices/by-tenant/{a}")));
        Assert.Contains(devB, await DeviceIdsAsync(await client.GetAsync("/api/devices/by-type/sensor")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/devices/statistics?tenantId={b}")).StatusCode);
        var stats = await (await client.GetAsync("/api/devices/statistics")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(stats.GetProperty("totalDevices").GetInt32() >= 3);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/devices/{idA}", new { name = "by-superadmin" })).StatusCode);
        Assert.Equal("by-superadmin", (await DeviceRowAsync(idA))!.Name);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/devices/{idB}/deactivate", null)).StatusCode);
        Assert.False((await DeviceRowAsync(idB))!.IsActive);
    }

    [Fact]
    public async Task SuperAdmin_UnknownDevice_And_UnknownTenant_KeepTheirNormalAnswers()
    {
        var client = SuperAdmin();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/devices/{Guid.NewGuid()}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/devices/{Guid.NewGuid()}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/devices", RegisterBody(Guid.NewGuid()))).StatusCode);
    }

    // ----- what does NOT count as membership or as a management role --------------------------------

    [Fact]
    public async Task ExpiredManagerRole_DoesNotCount()
    {
        var tenant = await CreateTenantAsync();
        var (id, deviceId) = await SeedDeviceAsync(tenant);
        var (_, client) = await UserWithRoleAsync("BuildingManager", tenant, expiresAt: DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/devices", RegisterBody(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/devices/{id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/by-device-id/{deviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/devices/by-tenant/{tenant}")).StatusCode);
        Assert.True((await DeviceRowAsync(id))!.IsActive);
    }

    [Fact]
    public async Task ManagerRoleWithoutATenant_DoesNotManageEveryTenant()
    {
        var tenant = await CreateTenantAsync();
        var (id, deviceId) = await SeedDeviceAsync(tenant);
        var (_, client) = await UserWithRoleAsync("BuildingManager", tenantId: null);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/devices", RegisterBody(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/devices/{id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/by-device-id/{deviceId}")).StatusCode);
        Assert.DoesNotContain(deviceId, await DeviceIdsAsync(await client.GetAsync("/api/devices")));
    }

    [Fact]
    public async Task RoleClaimAloneIsNotEnough_TheUserRolesRowDecides()
    {
        var tenant = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(tenant);
        // Role claims are global names: a token claiming BuildingManager but with no UserRoles row manages nothing.
        var client = ClientAs(await CreateUserAsync(), new[] { "BuildingManager", "BuildingOwner", "TenantAdmin" });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/devices", RegisterBody(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/devices/{id}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task TokenWithTenantClaim_OnlyActsInThatTenant()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var (idA, devA) = await SeedDeviceAsync(a);
        var (idB, devB) = await SeedDeviceAsync(b);
        var (userId, _) = await UserWithRoleAsync("BuildingManager", a);
        await GrantRoleAsync(userId, "BuildingManager", b);
        var client = ClientAs(userId, new[] { "BuildingManager" }, tenantClaim: a.ToString());

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/devices", RegisterBody(a))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/devices", RegisterBody(b))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/devices/{idA}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/devices/{idB}/deactivate", null)).StatusCode);
        Assert.True((await DeviceRowAsync(idB))!.IsActive);
        var all = await DeviceIdsAsync(await client.GetAsync("/api/devices"));
        Assert.Contains(devA, all);
        Assert.DoesNotContain(devB, all);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/by-device-id/{devB}")).StatusCode);
    }

    [Fact]
    public async Task TokenWithUnparseableTenantClaim_IsRefused()
    {
        var tenant = await CreateTenantAsync();
        var (userId, _) = await UserWithRoleAsync("BuildingManager", tenant);
        var client = ClientAs(userId, new[] { "BuildingManager" }, tenantClaim: "not-a-guid");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/devices", RegisterBody(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/devices")).StatusCode);
    }

    [Fact]
    public async Task ByType_WithoutTenant_ReturnsOnlyActiveDevicesOfTheCallersTenants_AsBefore()
    {
        var tenant = await CreateTenantAsync();
        var (_, activeDevice) = await SeedDeviceAsync(tenant, deviceType: "thermostat");
        var (_, inactiveDevice) = await SeedDeviceAsync(tenant, isActive: false, deviceType: "thermostat");
        var (_, client) = await UserWithRoleAsync("User", tenant);

        var ids = await DeviceIdsAsync(await client.GetAsync("/api/devices/by-type/thermostat"));

        Assert.Contains(activeDevice, ids);
        Assert.DoesNotContain(inactiveDevice, ids);
    }
}
