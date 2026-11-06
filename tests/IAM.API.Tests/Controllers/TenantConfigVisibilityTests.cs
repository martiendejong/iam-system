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
/// Task 5575: tenant Settings and Metadata are only returned to platform admins and to owners/managers of the tenant
/// (or of a tenant above it). A plain member still gets name, type and hierarchy on list, get, hierarchy and
/// building-structure, but no Settings or Metadata.
/// </summary>
public class TenantConfigVisibilityTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public TenantConfigVisibilityTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private IAMDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAMDbContext>();

    private async Task<Tenant> TenantAsync(string type, Guid? parentId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", Type = type, ParentTenantId = parentId,
            IsActive = true, Metadata = "{\"secret\":\"meta\"}", Settings = "{\"secret\":\"setting\"}"
        };
        Db(scope).Tenants.Add(tenant);
        await Db(scope).SaveChangesAsync();
        return tenant;
    }

    private async Task<HttpClient> MemberAsync(string roleName, Tenant tenant)
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = Db(scope);
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName && r.TenantId == null);
            if (role == null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = roleName, Permissions = "[\"Room.View\"]" };
                db.Roles.Add(role);
            }
            db.Users.Add(new User { Id = userId, Email = $"{userId:N}@tenant-config.test", PasswordHash = "x", IsActive = true });
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenant.Id, GrantedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        return ClientAs(userId, roleName);
    }

    private HttpClient ClientAs(Guid userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@tenant-config.test", roles, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static bool Present(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object;

    private static JsonElement FindById(JsonElement list, Guid id) =>
        list.EnumerateArray().First(e => e.GetProperty("id").GetGuid() == id);

    private static JsonElement FindInTree(JsonElement node, Guid id)
    {
        if (node.GetProperty("id").GetGuid() == id) return node;
        foreach (var c in node.GetProperty("children").EnumerateArray())
        {
            try { return FindInTree(c, id); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("not found");
    }

    /// <summary>Reads the same tenant through all four endpoints; returns (hasSettings, hasMetadata) per endpoint.</summary>
    private async Task<(bool Settings, bool Metadata)[]> ReadAllAsync(HttpClient client, Tenant building, Tenant floor)
    {
        var list = await (await client.GetAsync($"/api/tenants?parentId={floor.ParentTenantId}")).Content.ReadFromJsonAsync<JsonElement>();
        var listed = FindById(list, floor.Id);

        var getResponse = await client.GetAsync($"/api/tenants/{floor.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var got = await getResponse.Content.ReadFromJsonAsync<JsonElement>();

        var hierarchy = await (await client.GetAsync($"/api/tenants/{building.Id}/hierarchy")).Content.ReadFromJsonAsync<JsonElement>();
        var inTree = FindInTree(hierarchy, floor.Id);

        var structure = await (await client.GetAsync($"/api/tenants/buildings/{building.Id}/structure")).Content.ReadFromJsonAsync<JsonElement>();
        var structFloor = structure.GetProperty("building").GetProperty("floors").EnumerateArray()
            .First(f => f.GetProperty("id").GetGuid() == floor.Id);

        // Name/type/hierarchy are always present.
        Assert.Equal(floor.Name, listed.GetProperty("name").GetString());
        Assert.Equal("Floor", got.GetProperty("type").GetString());
        Assert.Equal(floor.Name, inTree.GetProperty("name").GetString());
        Assert.Equal(floor.Name, structFloor.GetProperty("name").GetString());

        return new[]
        {
            (Present(listed, "settings"), Present(listed, "metadata")),
            (Present(got, "settings"), Present(got, "metadata")),
            (Present(inTree, "settings"), Present(inTree, "metadata")),
            (false, Present(structFloor, "metadata")), // building-structure has no settings field
        };
    }

    [Fact]
    public async Task PlainMember_GetsNameAndHierarchy_ButNoSettingsOrMetadata_OnAllFourEndpoints()
    {
        var building = await TenantAsync("Building");
        var floor = await TenantAsync("Floor", building.Id);
        var member = await MemberAsync("Resident", building);

        foreach (var (settings, metadata) in await ReadAllAsync(member, building, floor))
        {
            Assert.False(settings);
            Assert.False(metadata);
        }

        // The building itself too.
        var self = await (await member.GetAsync($"/api/tenants/{building.Id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(Present(self, "settings"));
        Assert.False(Present(self, "metadata"));
    }

    [Theory]
    [InlineData("BuildingOwner")]
    [InlineData("BuildingManager")]
    public async Task ManagerOfTheBuilding_GetsBoth_AlsoForTenantsBelowIt(string role)
    {
        var building = await TenantAsync("Building");
        var floor = await TenantAsync("Floor", building.Id);
        var manager = await MemberAsync(role, building);

        var results = await ReadAllAsync(manager, building, floor);
        Assert.All(results.Take(3), r => { Assert.True(r.Settings); Assert.True(r.Metadata); });
        Assert.True(results[3].Metadata);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    public async Task PlatformAdmin_GetsBoth(string role)
    {
        var building = await TenantAsync("Building");
        var floor = await TenantAsync("Floor", building.Id);

        var results = await ReadAllAsync(ClientAs(Guid.NewGuid(), role), building, floor);
        Assert.All(results.Take(3), r => { Assert.True(r.Settings); Assert.True(r.Metadata); });
        Assert.True(results[3].Metadata);
    }

    [Fact]
    public async Task ManagerOfAFloor_GetsTheFloorsConfig()
    {
        var building = await TenantAsync("Building");
        var floor = await TenantAsync("Floor", building.Id);
        var floorManager = await MemberAsync("BuildingManager", floor);

        var self = await (await floorManager.GetAsync($"/api/tenants/{floor.Id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(Present(self, "settings"));
        Assert.True(Present(self, "metadata"));
    }
}
