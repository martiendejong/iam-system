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
/// Task 5154: tenant update and create-under-a-parent are limited to a SuperAdmin or a BuildingOwner/BuildingManager of
/// that tenant (or of a tenant above it), and tenant list, get, hierarchy and building-structure only return the tenants
/// the caller belongs to (platform admins see all); any other id answers 404.
/// </summary>
public class TenantScopeAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public TenantScopeAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

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

    // ---- update ------------------------------------------------------------------------------

    [Theory]
    [InlineData("BuildingManager")]
    [InlineData("BuildingOwner")]
    public async Task ManagerOfTenantA_CannotUpdateTenantB_ButCanUpdateOwnTenant(string role)
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var manager = await MemberAsync(role, a);

        var foreign = await manager.PutAsJsonAsync($"/api/tenants/{b.Id}", Rename("hijacked"));
        Assert.True(foreign.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"was {foreign.StatusCode}");
        Assert.NotEqual("hijacked", await NameInDbAsync(b.Id));

        var own = await manager.PutAsJsonAsync($"/api/tenants/{a.Id}", Rename("renamed by own manager"));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("renamed by own manager", await NameInDbAsync(a.Id));
    }

    [Fact]
    public async Task PlatformAdmin_AndManagerOfB_StillUpdateB()
    {
        var b = await TenantAsync();
        var managerOfB = await MemberAsync("BuildingManager", b);

        Assert.Equal(HttpStatusCode.OK, (await Admin().PutAsJsonAsync($"/api/tenants/{b.Id}", Rename("by superadmin"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerOfB.PutAsJsonAsync($"/api/tenants/{b.Id}", Rename("by manager of b"))).StatusCode);
        Assert.Equal("by manager of b", await NameInDbAsync(b.Id));
    }

    [Fact]
    public async Task OwnerOfABuilding_CanUpdateAFloorBelowIt_ButNotAnUnrelatedTenant()
    {
        var building = await TenantAsync("Building");
        var floor = await TenantAsync("Floor", building.Id);
        var other = await TenantAsync("Building");
        var owner = await MemberAsync("BuildingOwner", building);

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/tenants/{floor.Id}", Rename("floor 1"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/tenants/{other.Id}", Rename("nope"))).StatusCode);
    }

    [Fact]
    public async Task ARoleClaimAloneOrARoleInAnotherTenant_ConfersNothing()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var roleless = ClientAs(Guid.NewGuid(), "BuildingManager"); // role claim only, no row anywhere

        Assert.NotEqual(HttpStatusCode.OK, (await roleless.PutAsJsonAsync($"/api/tenants/{a.Id}", Rename("x"))).StatusCode);

        var manager = await MemberAsync("BuildingManager", a);
        Assert.NotEqual(HttpStatusCode.OK, (await manager.PutAsJsonAsync($"/api/tenants/{b.Id}", Rename("x"))).StatusCode);
    }

    // ---- create ------------------------------------------------------------------------------

    [Fact]
    public async Task Create_UnderAParentTheCallerDoesNotManage_IsRefused_UnderAManagedParentWorks()
    {
        var mine = await TenantAsync("Building");
        var theirs = await TenantAsync("Building");
        var owner = await MemberAsync("BuildingOwner", mine);

        var refused = await owner.PostAsJsonAsync("/api/tenants", new { name = "sneaky floor", type = "Floor", parentTenantId = theirs.Id });
        Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"was {refused.StatusCode}");

        var allowed = await owner.PostAsJsonAsync("/api/tenants", new { name = "my floor", type = "Floor", parentTenantId = mine.Id });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        var superAdmin = await Admin().PostAsJsonAsync("/api/tenants", new { name = "admin floor", type = "Floor", parentTenantId = theirs.Id });
        Assert.Equal(HttpStatusCode.Created, superAdmin.StatusCode);

        using var scope = _factory.Services.CreateScope();
        Assert.False(await Db(scope).Tenants.AnyAsync(t => t.Name == "sneaky floor"));
    }

    [Fact]
    public async Task Create_RootTenant_StaysAsBefore_ForABuildingOwner()
    {
        var mine = await TenantAsync("Building");
        var owner = await MemberAsync("BuildingOwner", mine);

        var response = await owner.PostAsJsonAsync("/api/tenants", new { name = "a root", type = "Organization" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- read --------------------------------------------------------------------------------

    [Fact]
    public async Task List_OnlyReturnsTheCallersTenantsAndTheirDescendants_PlatformAdminSeesAll()
    {
        var mine = await TenantAsync("Building");
        var floor = await TenantAsync("Floor", mine.Id);
        var theirs = await TenantAsync("Building");
        var member = await MemberAsync("BuildingManager", mine);

        var scoped = await IdsAsync(await member.GetAsync("/api/tenants"));
        Assert.Contains(mine.Id, scoped);
        Assert.DoesNotContain(theirs.Id, scoped);

        var children = await IdsAsync(await member.GetAsync($"/api/tenants?parentId={mine.Id}"));
        Assert.Equal(new[] { floor.Id }, children);

        var foreignChildren = await IdsAsync(await member.GetAsync($"/api/tenants?parentId={theirs.Id}"));
        Assert.Empty(foreignChildren);

        var all = await IdsAsync(await Admin().GetAsync("/api/tenants"));
        Assert.Contains(theirs.Id, all);
        Assert.Contains(mine.Id, all);
    }

    [Fact]
    public async Task List_ForAScopedCaller_DoesNotDiscloseTheParentOutsideTheirScope()
    {
        var org = await TenantAsync("Organization", name: "Customer Org Secret Name");
        var building = await TenantAsync("Building", org.Id);
        var member = await MemberAsync("BuildingManager", building);

        var body = await (await member.GetAsync("/api/tenants")).Content.ReadAsStringAsync();

        Assert.Contains(building.Id.ToString(), body);
        Assert.DoesNotContain("Customer Org Secret Name", body);
        Assert.DoesNotContain(org.Id.ToString(), body);
    }

    [Fact]
    public async Task Get_Hierarchy_AndBuildingStructure_OtherTenantsLookLikeTheyDoNotExist()
    {
        var mine = await TenantAsync("Building");
        var theirs = await TenantAsync("Building");
        var member = await MemberAsync("BuildingManager", mine);

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/tenants/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/tenants/{mine.Id}/hierarchy")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/tenants/buildings/{mine.Id}/structure")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/tenants/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/tenants/{theirs.Id}/hierarchy")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/tenants/buildings/{theirs.Id}/structure")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/tenants/{Guid.NewGuid()}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync($"/api/tenants/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin("SystemAdmin").GetAsync($"/api/tenants/{theirs.Id}/hierarchy")).StatusCode);
    }

    [Fact]
    public async Task UserWithNoTenantMembership_SeesNoTenants_AndMyTenantsIsUnchanged()
    {
        var mine = await TenantAsync();
        var member = await MemberAsync("BuildingManager", mine);
        var nobody = ClientAs(Guid.NewGuid(), "User");

        Assert.Empty(await IdsAsync(await nobody.GetAsync("/api/tenants")));
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"/api/tenants/{mine.Id}")).StatusCode);

        var mineList = await IdsAsync(await member.GetAsync("/api/tenants/my-tenants"));
        Assert.Equal(new[] { mine.Id }, mineList);
    }
}
