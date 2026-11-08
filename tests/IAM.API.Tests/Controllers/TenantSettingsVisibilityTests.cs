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
/// Task 5575: Settings and Metadata of a tenant are only returned to platform admins and owners/managers of the tenant
/// (or one above it); a plain member still gets name, type and hierarchy.
/// </summary>
public class TenantSettingsVisibilityTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public TenantSettingsVisibilityTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private IAMDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAMDbContext>();

    private async Task<Tenant> TenantAsync(string type = "Organization", Guid? parentId = null, string? name = null)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(), Name = name ?? $"Tenant {Guid.NewGuid():N}", Type = type, ParentTenantId = parentId,
            IsActive = true, Metadata = "{\"secret\":\"meta\"}", Settings = "{\"secret\":\"setting\"}"
        };
        Db(scope).Tenants.Add(tenant);
        await Db(scope).SaveChangesAsync();
        return tenant;
    }

    private async Task<Role> RoleAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var existing = await Db(scope).Roles.FirstOrDefaultAsync(r => r.Name == name && r.TenantId == null);
        if (existing != null) return existing;
        var role = new Role { Id = Guid.NewGuid(), Name = name, Permissions = "[\"Room.View\"]" };
        Db(scope).Roles.Add(role);
        await Db(scope).SaveChangesAsync();
        return role;
    }

    /// <summary>A user holding the role in the tenant, and a client whose token carries the role claim.</summary>
    private async Task<HttpClient> MemberAsync(string roleName, Tenant tenant)
    {
        var role = await RoleAsync(roleName);
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            Db(scope).Users.Add(new User { Id = userId, Email = $"{userId:N}@tenant-scope.test", PasswordHash = "x", IsActive = true });
            Db(scope).UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenant.Id, GrantedAt = DateTime.UtcNow
            });
            await Db(scope).SaveChangesAsync();
        }
        return ClientAs(userId, roleName);
    }

    private HttpClient ClientAs(Guid userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@tenant-scope.test", roles, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient Admin(string role = "SuperAdmin") => ClientAs(Guid.NewGuid(), role);

    private static object Rename(string name) => new { name };

    private async Task<string?> NameInDbAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await Db(scope).Tenants.AsNoTracking().Where(t => t.Id == id).Select(t => t.Name).FirstOrDefaultAsync();
    }

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    private static bool Has(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("secret", out _);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement FindInList(JsonElement list, Guid id) =>
        list.EnumerateArray().First(e => e.GetProperty("id").GetGuid() == id);

    public static IEnumerable<object[]> Callers => new[]
    {
        new object[] { "member", false },
        new object[] { "BuildingManager", true },
        new object[] { "BuildingOwner", true },
        new object[] { "SuperAdmin", true },
    };

    private async Task<HttpClient> CallerAsync(string kind, Tenant tenant) =>
        kind == "SuperAdmin" ? Admin() : await MemberAsync(kind == "member" ? "Viewer" : kind, tenant);

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task ListGetAndHierarchy_ReturnSettingsAndMetadataOnlyToManagers(string kind, bool expected)
    {
        var building = await TenantAsync("Building");
        var floor = await TenantAsync("Floor", building.Id);
        var client = await CallerAsync(kind, building);

        var list = await JsonAsync(await client.GetAsync("/api/tenants?parentId=" + building.Id));
        var listed = FindInList(list, floor.Id);
        Assert.Equal(expected, Has(listed, "settings"));
        Assert.Equal(expected, Has(listed, "metadata"));
        Assert.Equal(floor.Name, listed.GetProperty("name").GetString());

        var get = await JsonAsync(await client.GetAsync($"/api/tenants/{building.Id}"));
        Assert.Equal(expected, Has(get, "settings"));
        Assert.Equal(expected, Has(get, "metadata"));
        Assert.Equal(building.Name, get.GetProperty("name").GetString());

        var hierarchy = await JsonAsync(await client.GetAsync($"/api/tenants/{building.Id}/hierarchy"));
        Assert.Equal(expected, Has(hierarchy, "settings"));
        Assert.Equal(expected, Has(hierarchy, "metadata"));
        var child = hierarchy.GetProperty("children").EnumerateArray().Single();
        Assert.Equal(expected, Has(child, "settings"));
        Assert.Equal(expected, Has(child, "metadata"));
        Assert.Equal(floor.Name, child.GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task BuildingStructure_ReturnsMetadataOnlyToManagers(string kind, bool expected)
    {
        var building = await TenantAsync("Building");
        var floor = await TenantAsync("Floor", building.Id);
        var room = await TenantAsync("Room", floor.Id);
        var client = await CallerAsync(kind, building);

        var json = await JsonAsync(await client.GetAsync($"/api/tenants/buildings/{building.Id}/structure"));
        var b = json.GetProperty("building");
        Assert.Equal(expected, Has(b, "metadata"));
        var f = b.GetProperty("floors").EnumerateArray().Single();
        Assert.Equal(expected, Has(f, "metadata"));
        Assert.Equal(floor.Name, f.GetProperty("name").GetString());
        var r = f.GetProperty("rooms").EnumerateArray().Single();
        Assert.Equal(expected, Has(r, "metadata"));
        Assert.Equal(room.Name, r.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ManagerOfTheBuilding_SeesSettingsOfFloorBelow_ButManagerOfFloorNotOfTheBuildingAbove()
    {
        var building = await TenantAsync("Building");
        var floor = await TenantAsync("Floor", building.Id);

        var owner = await MemberAsync("BuildingOwner", building);
        Assert.True(Has(await JsonAsync(await owner.GetAsync($"/api/tenants/{floor.Id}")), "settings"));

        var floorManager = await MemberAsync("BuildingManager", floor);
        Assert.True(Has(await JsonAsync(await floorManager.GetAsync($"/api/tenants/{floor.Id}")), "settings"));
        var hierarchy = await JsonAsync(await floorManager.GetAsync($"/api/tenants/{floor.Id}/hierarchy"));
        Assert.True(Has(hierarchy, "metadata"));
    }

    [Fact]
    public async Task ManagerOfTenantA_DoesNotSeeSettingsOfUnrelatedTenantB_WhereTheyAreOnlyAMember()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var manager = await MemberAsync("BuildingManager", a);
        using (var scope = _factory.Services.CreateScope())
        {
            var userId = Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(manager.DefaultRequestHeaders.Authorization!.Parameter!).Subject);
            var viewer = await RoleAsync("Viewer");
            Db(scope).UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = viewer.Id, TenantId = b.Id, GrantedAt = DateTime.UtcNow });
            await Db(scope).SaveChangesAsync();
        }

        Assert.True(Has(await JsonAsync(await manager.GetAsync($"/api/tenants/{a.Id}")), "settings"));
        var asMember = await JsonAsync(await manager.GetAsync($"/api/tenants/{b.Id}"));
        Assert.False(Has(asMember, "settings"));
        Assert.False(Has(asMember, "metadata"));
    }
}
