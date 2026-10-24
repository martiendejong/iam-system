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
/// Task 5164: the building-management API (Location, Building, Floor, Room, RoomGroup, IoTDevice). Device,
/// service-account and token-exchange tokens are refused on every action; reading needs membership of the tenant,
/// changing needs SuperAdmin or a building-management role in it; the tenant comes from the caller's authorization
/// (never the body); create/update bind input models; parents must belong to the same tenant.
/// </summary>
public class BuildingManagementAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IAMTestWebApplicationFactory _factory;

    public BuildingManagementAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    // ----- seeding helpers -------------------------------------------------------------------

    private sealed record Hierarchy(Guid TenantId, Guid LocationId, Guid BuildingId, Guid FloorId, Guid RoomId, Guid GroupId, Guid DeviceId, string DeviceKey);

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
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@building-authz.test", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task GrantRoleAsync(Guid userId, string roleName, Guid tenantId)
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
            GrantedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<Hierarchy> SeedHierarchyAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var location = new Location { Name = "Seeded location", TenantId = tenantId };
        var building = new Building { Name = "Seeded building", LocationId = location.Id, TenantId = tenantId };
        var floor = new Floor { Name = "Seeded floor", BuildingId = building.Id, TenantId = tenantId };
        var room = new Room { Name = "Seeded room", FloorId = floor.Id, TenantId = tenantId };
        var group = new RoomGroup { Name = "Seeded group", TenantId = tenantId };
        var device = new IoTDevice
        {
            Name = "Seeded device",
            DeviceId = $"dev-{Guid.NewGuid():N}",
            RoomId = room.Id,
            TenantId = tenantId
        };

        db.Locations.Add(location);
        db.Buildings.Add(building);
        db.Floors.Add(floor);
        db.Rooms.Add(room);
        db.RoomGroups.Add(group);
        db.IoTDevices.Add(device);
        await db.SaveChangesAsync();

        return new Hierarchy(tenantId, location.Id, building.Id, floor.Id, room.Id, group.Id, device.Id, device.DeviceId);
    }

    private async Task<T?> RowAsync<T>(Guid id) where T : class
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Set<T>().AsNoTracking().FirstOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id);
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A person holding the given role in the given tenant; the token has no tenant_id, like a password login.</summary>
    private async Task<(Guid UserId, HttpClient Client)> PersonWithRoleAsync(string roleName, Guid tenantId, string? tenantClaim = null)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, roleName, tenantId);
        return (userId, ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@building-authz.test", new[] { roleName }, tenantClaim)));
    }

    private HttpClient SuperAdmin() =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(Guid.NewGuid(), "super@building-authz.test", new[] { "SuperAdmin" }, null));

    // ----- every endpoint, aimed at one hierarchy --------------------------------------------

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    private static (string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)[] AllEndpoints(Hierarchy h, string suffix = "")
    {
        var q = suffix;
        var location = new { name = "L", city = "X" };
        var building = new { name = "B", locationId = h.LocationId };
        var floor = new { name = "F", floorNumber = 1, buildingId = h.BuildingId };
        var room = new { name = "R", floorId = h.FloorId };
        var group = new { name = "G", floorId = h.FloorId, buildingId = h.BuildingId };
        var device = new { name = "D", deviceId = $"x-{Guid.NewGuid():N}", roomId = h.RoomId };

        return new (string, Func<HttpClient, Task<HttpResponseMessage>>)[]
        {
            ("location list", c => c.GetAsync($"/api/location{Q(q)}")),
            ("location get", c => c.GetAsync($"/api/location/{h.LocationId}{Q(q)}")),
            ("location create", c => c.PostAsync($"/api/location{Q(q)}", Json(location))),
            ("location update", c => c.PutAsync($"/api/location/{h.LocationId}{Q(q)}", Json(location))),
            ("location delete", c => c.DeleteAsync($"/api/location/{h.LocationId}{Q(q)}")),
            ("location buildings", c => c.GetAsync($"/api/location/{h.LocationId}/buildings{Q(q)}")),

            ("building list", c => c.GetAsync($"/api/building{Q(q)}")),
            ("building get", c => c.GetAsync($"/api/building/{h.BuildingId}{Q(q)}")),
            ("building by location", c => c.GetAsync($"/api/building/location/{h.LocationId}{Q(q)}")),
            ("building create", c => c.PostAsync($"/api/building{Q(q)}", Json(building))),
            ("building update", c => c.PutAsync($"/api/building/{h.BuildingId}{Q(q)}", Json(building))),
            ("building delete", c => c.DeleteAsync($"/api/building/{h.BuildingId}{Q(q)}")),
            ("building floors", c => c.GetAsync($"/api/building/{h.BuildingId}/floors{Q(q)}")),

            ("floor list", c => c.GetAsync($"/api/floor{Q(q)}")),
            ("floor get", c => c.GetAsync($"/api/floor/{h.FloorId}{Q(q)}")),
            ("floor by building", c => c.GetAsync($"/api/floor/building/{h.BuildingId}{Q(q)}")),
            ("floor create", c => c.PostAsync($"/api/floor{Q(q)}", Json(floor))),
            ("floor update", c => c.PutAsync($"/api/floor/{h.FloorId}{Q(q)}", Json(floor))),
            ("floor delete", c => c.DeleteAsync($"/api/floor/{h.FloorId}{Q(q)}")),
            ("floor rooms", c => c.GetAsync($"/api/floor/{h.FloorId}/rooms{Q(q)}")),

            ("room list", c => c.GetAsync($"/api/room{Q(q)}")),
            ("room get", c => c.GetAsync($"/api/room/{h.RoomId}{Q(q)}")),
            ("room by floor", c => c.GetAsync($"/api/room/floor/{h.FloorId}{Q(q)}")),
            ("room by type", c => c.GetAsync($"/api/room/type/Office{Q(q)}")),
            ("room create", c => c.PostAsync($"/api/room{Q(q)}", Json(room))),
            ("room update", c => c.PutAsync($"/api/room/{h.RoomId}{Q(q)}", Json(room))),
            ("room delete", c => c.DeleteAsync($"/api/room/{h.RoomId}{Q(q)}")),
            ("room devices", c => c.GetAsync($"/api/room/{h.RoomId}/devices{Q(q)}")),
            ("room groups", c => c.GetAsync($"/api/room/{h.RoomId}/groups{Q(q)}")),

            ("group list", c => c.GetAsync($"/api/roomgroup{Q(q)}")),
            ("group get", c => c.GetAsync($"/api/roomgroup/{h.GroupId}{Q(q)}")),
            ("group by floor", c => c.GetAsync($"/api/roomgroup/floor/{h.FloorId}{Q(q)}")),
            ("group by building", c => c.GetAsync($"/api/roomgroup/building/{h.BuildingId}{Q(q)}")),
            ("group create", c => c.PostAsync($"/api/roomgroup{Q(q)}", Json(group))),
            ("group update", c => c.PutAsync($"/api/roomgroup/{h.GroupId}{Q(q)}", Json(group))),
            ("group delete", c => c.DeleteAsync($"/api/roomgroup/{h.GroupId}{Q(q)}")),
            ("group add room", c => c.PostAsync($"/api/roomgroup/{h.GroupId}/rooms/{h.RoomId}{Q(q)}", null)),
            ("group remove room", c => c.DeleteAsync($"/api/roomgroup/{h.GroupId}/rooms/{h.RoomId}{Q(q)}")),
            ("group rooms", c => c.GetAsync($"/api/roomgroup/{h.GroupId}/rooms{Q(q)}")),

            ("iot list", c => c.GetAsync($"/api/iotdevice{Q(q)}")),
            ("iot get", c => c.GetAsync($"/api/iotdevice/{h.DeviceId}{Q(q)}")),
            ("iot by device id", c => c.GetAsync($"/api/iotdevice/device-id/{h.DeviceKey}{Q(q)}")),
            ("iot by room", c => c.GetAsync($"/api/iotdevice/room/{h.RoomId}{Q(q)}")),
            ("iot by type", c => c.GetAsync($"/api/iotdevice/type/Sensor{Q(q)}")),
            ("iot by status", c => c.GetAsync($"/api/iotdevice/status/Online{Q(q)}")),
            ("iot streaming", c => c.GetAsync($"/api/iotdevice/streaming{Q(q)}")),
            ("iot create", c => c.PostAsync($"/api/iotdevice{Q(q)}", Json(device))),
            ("iot update", c => c.PutAsync($"/api/iotdevice/{h.DeviceId}{Q(q)}", Json(device))),
            ("iot delete", c => c.DeleteAsync($"/api/iotdevice/{h.DeviceId}{Q(q)}")),
            ("iot status", c => c.PatchAsync($"/api/iotdevice/{h.DeviceId}/status{Q(q)}", Json(0))),
            ("iot heartbeat", c => c.PostAsync($"/api/iotdevice/{h.DeviceId}/heartbeat{Q(q)}", null)),
            ("iot logs", c => c.GetAsync($"/api/iotdevice/{h.DeviceId}/logs{Q(q)}")),
            ("iot stream", c => c.PostAsync($"/api/iotdevice/{h.DeviceId}/stream{Q(q)}", null)),
            ("iot control", c => c.PostAsync($"/api/iotdevice/{h.DeviceId}/control{Q(q)}", Json(new { })))
        };

        static string Q(string s) => s;
    }

    private static readonly string[] WriteEndpointNames =
    {
        "location create", "location update", "location delete",
        "building create", "building update", "building delete",
        "floor create", "floor update", "floor delete",
        "room create", "room update", "room delete",
        "group create", "group update", "group delete", "group add room", "group remove room",
        "iot create", "iot update", "iot delete", "iot status", "iot heartbeat"
    };

    private async Task AssertNothingChangedAsync(Hierarchy h)
    {
        Assert.Equal("Seeded location", (await RowAsync<Location>(h.LocationId))!.Name);
        Assert.True((await RowAsync<Location>(h.LocationId))!.IsActive);
        Assert.True((await RowAsync<Building>(h.BuildingId))!.IsActive);
        Assert.True((await RowAsync<Floor>(h.FloorId))!.IsActive);
        Assert.True((await RowAsync<Room>(h.RoomId))!.IsActive);
        Assert.True((await RowAsync<RoomGroup>(h.GroupId))!.IsActive);
        var device = (await RowAsync<IoTDevice>(h.DeviceId))!;
        Assert.True(device.IsActive);
        Assert.Equal("Seeded device", device.Name);
        Assert.Equal(DeviceStatus.Offline, device.Status);
        Assert.Null(device.LastSeenAt);
    }

    private async Task AssertRefusedEverywhereAsync(HttpClient client, Hierarchy h)
    {
        foreach (var (name, call) in AllEndpoints(h))
        {
            var response = await call(client);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name} returned {(int)response.StatusCode}");
        }
        await AssertNothingChangedAsync(h);
    }

    // ----- callers that are refused outright ---------------------------------------------------

    [Fact]
    public async Task Anonymous_Gets401_OnEveryEndpoint()
    {
        var h = await SeedHierarchyAsync(await CreateTenantAsync());

        foreach (var (name, call) in AllEndpoints(h))
        {
            var response = await call(_factory.CreateClient());
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name} returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task DeviceToken_Gets403_OnEveryEndpoint_AndNothingChanges()
    {
        var tenant = await CreateTenantAsync();
        var h = await SeedHierarchyAsync(tenant);
        // The token of a device of this very tenant, carrying the tenant_id the controllers used to trust.
        var client = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(h.DeviceId, h.DeviceKey, tenant));

        await AssertRefusedEverywhereAsync(client, h);
    }

    [Fact]
    public async Task DeviceToken_WithoutTenantClaim_Gets403_Too()
    {
        var tenant = await CreateTenantAsync();
        var h = await SeedHierarchyAsync(tenant);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(h.DeviceKey, null));

        await AssertRefusedEverywhereAsync(client, h);
    }

    [Fact]
    public async Task ServiceAccountToken_Gets403_OnEveryEndpoint()
    {
        var h = await SeedHierarchyAsync(await CreateTenantAsync());
        var client = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc-building", "building:manage", "device:manage"));

        await AssertRefusedEverywhereAsync(client, h);
    }

    [Fact]
    public async Task TokenExchangeToken_Gets403_EvenForTenantAdminRole()
    {
        var tenant = await CreateTenantAsync();
        var h = await SeedHierarchyAsync(tenant);
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", tenant);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateTokenExchangeToken(userId, tenant, "TenantAdmin"));

        await AssertRefusedEverywhereAsync(client, h);
    }

    // ----- people ------------------------------------------------------------------------------

    [Fact]
    public async Task TenantAdmin_CanReadAndChangeOwnTenant_WithAPasswordLoginToken()
    {
        var tenant = await CreateTenantAsync();
        var h = await SeedHierarchyAsync(tenant);
        var (_, client) = await PersonWithRoleAsync("TenantAdmin", tenant);

        // Deletes last: they soft-delete the very resources the other calls aim at.
        foreach (var (name, call) in AllEndpoints(h).Where(e => !e.Name.StartsWith("iot logs") && !e.Name.StartsWith("iot stream") && !e.Name.StartsWith("iot control")).OrderBy(e => e.Name.EndsWith("delete")))
        {
            var response = await call(client);
            Assert.True((int)response.StatusCode < 400, $"{name} returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task BuildingManager_CanChange_ButAPlainMember_CanOnlyRead()
    {
        var tenant = await CreateTenantAsync();
        var h = await SeedHierarchyAsync(tenant);
        var (_, manager) = await PersonWithRoleAsync("BuildingManager", tenant);
        var (_, member) = await PersonWithRoleAsync("User", tenant);

        var managerCreate = await manager.PostAsync("/api/location", Json(new { name = "by manager" }));
        Assert.Equal(HttpStatusCode.Created, managerCreate.StatusCode);

        foreach (var (name, call) in AllEndpoints(h))
        {
            var response = await call(member);
            if (WriteEndpointNames.Contains(name))
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name} returned {(int)response.StatusCode}");
            else if (!name.StartsWith("iot logs") && !name.StartsWith("iot stream") && !name.StartsWith("iot control"))
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{name} returned {(int)response.StatusCode}");
        }
        await AssertNothingChangedAsync(h);
    }

    [Fact]
    public async Task AdminOfTenantA_GetsForbidden_ForTenantB_ByQuery_ByClaim_AndNotFound_ForBsIds()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var a = await SeedHierarchyAsync(tenantA);
        var b = await SeedHierarchyAsync(tenantB);
        var (_, adminA) = await PersonWithRoleAsync("TenantAdmin", tenantA);
        var (_, adminAScoped) = await PersonWithRoleAsync("TenantAdmin", tenantA, tenantClaim: tenantB.ToString());

        // Naming the other tenant is refused everywhere.
        foreach (var (name, call) in AllEndpoints(b, $"?tenantId={tenantB}").Select(e => (e.Name, e.Call)))
        {
            var response = await call(adminA);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name} (query) returned {(int)response.StatusCode}");
        }
        // A token pinned to the other tenant has no authority there.
        foreach (var (name, call) in AllEndpoints(b))
        {
            var response = await call(adminAScoped);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name} (claim) returned {(int)response.StatusCode}");
        }
        // Without naming it, B's ids simply do not exist for A.
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/api/location/{b.LocationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/api/iotdevice/{b.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.DeleteAsync($"/api/building/{b.BuildingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.PutAsync($"/api/room/{b.RoomId}", Json(new { name = "x", floorId = a.FloorId }))).StatusCode);
        await AssertNothingChangedAsync(b);
    }

    [Fact]
    public async Task PersonInNoTenant_Gets403()
    {
        var h = await SeedHierarchyAsync(await CreateTenantAsync());
        var userId = await CreateUserAsync();
        var client = ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, "nobody@building-authz.test", new[] { "TenantAdmin" }, null));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/location")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/location", Json(new { name = "x" }))).StatusCode);
        await AssertNothingChangedAsync(h);
    }

    [Fact]
    public async Task PersonInTwoTenants_MustNameTheTenant()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", tenantA);
        await GrantRoleAsync(userId, "TenantAdmin", tenantB);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, "two@building-authz.test", new[] { "TenantAdmin" }, null));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/location")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/location?tenantId={tenantB}")).StatusCode);
        var created = await client.PostAsync($"/api/location?tenantId={tenantB}", Json(new { name = "in B" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(tenantB, json.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task SuperAdmin_NeedsTheTenant_ThenCanManageIt()
    {
        var tenant = await CreateTenantAsync();
        var client = SuperAdmin();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/location")).StatusCode);
        var created = await client.PostAsync($"/api/location?tenantId={tenant}", Json(new { name = "by super" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    // ----- input models ------------------------------------------------------------------------

    [Fact]
    public async Task Create_IgnoresIdTenantTimestampsAndNavigationCollections_FromTheBody()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var (_, admin) = await PersonWithRoleAsync("TenantAdmin", tenant);
        var smuggledId = Guid.NewGuid();

        var response = await admin.PostAsync("/api/location", Json(new
        {
            id = smuggledId,
            name = "Campus",
            tenantId = otherTenant,
            createdAt = "2001-01-01T00:00:00Z",
            updatedAt = "2002-02-02T00:00:00Z",
            isActive = false,
            buildings = new[] { new { name = "smuggled building", tenantId = otherTenant } }
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>());
        var id = created.GetProperty("id").GetGuid();
        Assert.NotEqual(smuggledId, id);
        var row = (await RowAsync<Location>(id))!;
        Assert.Equal(tenant, row.TenantId);
        Assert.True(row.IsActive);
        Assert.True(row.CreatedAt > DateTime.UtcNow.AddMinutes(-5));
        Assert.Null(row.UpdatedAt);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        Assert.False(await db.Buildings.AnyAsync(b => b.Name == "smuggled building"));
    }

    [Fact]
    public async Task Update_CannotMoveAResourceToAnotherTenant_OrChangeItsIdentityFields()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var h = await SeedHierarchyAsync(tenant);
        var (_, admin) = await PersonWithRoleAsync("TenantAdmin", tenant);
        var createdBefore = (await RowAsync<IoTDevice>(h.DeviceId))!.CreatedAt;

        var response = await admin.PutAsync($"/api/iotdevice/{h.DeviceId}", Json(new
        {
            id = Guid.NewGuid(),
            name = "Renamed",
            deviceId = h.DeviceKey,
            roomId = h.RoomId,
            tenantId = otherTenant,
            createdAt = "2001-01-01T00:00:00Z",
            isActive = false,
            status = (int)DeviceStatus.Error,
            lastSeenAt = "2030-01-01T00:00:00Z"
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = (await RowAsync<IoTDevice>(h.DeviceId))!;
        Assert.Equal("Renamed", row.Name);
        Assert.Equal(tenant, row.TenantId);
        Assert.True(row.IsActive);
        Assert.Equal(DeviceStatus.Offline, row.Status);
        Assert.Null(row.LastSeenAt);
        Assert.Equal(createdBefore, row.CreatedAt);
        Assert.NotNull(row.UpdatedAt);
    }

    [Fact]
    public async Task Create_RefusesAParentOfAnotherTenant()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var b = await SeedHierarchyAsync(tenantB);
        var (_, adminA) = await PersonWithRoleAsync("TenantAdmin", tenantA);

        Assert.Equal(HttpStatusCode.BadRequest, (await adminA.PostAsync("/api/building", Json(new { name = "x", locationId = b.LocationId }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminA.PostAsync("/api/floor", Json(new { name = "x", buildingId = b.BuildingId }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminA.PostAsync("/api/room", Json(new { name = "x", floorId = b.FloorId }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminA.PostAsync("/api/roomgroup", Json(new { name = "x", floorId = b.FloorId }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminA.PostAsync("/api/iotdevice", Json(new { name = "x", deviceId = "d-1", roomId = b.RoomId }))).StatusCode);
    }

    [Fact]
    public async Task Admin_CanBuildTheWholeHierarchy_AndRemoveIt()
    {
        var tenant = await CreateTenantAsync();
        var (_, admin) = await PersonWithRoleAsync("TenantAdmin", tenant);

        async Task<Guid> Post(string url, object body)
        {
            var response = await admin.PostAsync(url, Json(body));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        var location = await Post("/api/location", new { name = "Campus" });
        var building = await Post("/api/building", new { name = "A", locationId = location });
        var floor = await Post("/api/floor", new { name = "Ground", floorNumber = 0, buildingId = building });
        var room = await Post("/api/room", new { name = "101", floorId = floor });
        var group = await Post("/api/roomgroup", new { name = "Wing", floorId = floor });
        var device = await Post("/api/iotdevice", new { name = "Sensor", deviceId = $"s-{Guid.NewGuid():N}", roomId = room });

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/roomgroup/{group}/rooms/{room}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsync($"/api/iotdevice/{device}/status", Json(0))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/iotdevice/{device}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/iotdevice/{device}")).StatusCode);
    }

    [Fact]
    public async Task Create_RejectsAnInvalidBody_With400()
    {
        var tenant = await CreateTenantAsync();
        var (_, admin) = await PersonWithRoleAsync("TenantAdmin", tenant);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/location", Json(new { name = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/location", Json(new { name = "x", latitude = 400 }))).StatusCode);
    }
}
