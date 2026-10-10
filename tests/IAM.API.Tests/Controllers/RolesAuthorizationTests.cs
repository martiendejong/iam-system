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
/// Task 5163: who can read the role catalog and its holders, and what a BuildingOwner may create.
/// Reading (list and detail) is for administrators only, and the holders (names, e-mails) are limited to the tenants
/// the caller administers, so a tenant administrator never learns who holds SuperAdmin. Creating: a SuperAdmin may
/// create anything; a BuildingOwner only a plain role in a tenant they own (tenant from their own BuildingOwner rows,
/// not from the request): no global role, no other tenant, no "app:" category (it would lock every user without one of
/// that app's roles out of its SSO), no wildcard permissions, no platform role name. API-key callers are covered by
/// <see cref="RolesApiKeyAccessTests"/>.
/// </summary>
public class RolesAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public RolesAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

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

    private async Task<(Guid Id, string Email)> CreateUserAsync(string? emailPrefix = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{emailPrefix ?? "u"}-{Guid.NewGuid():N}@roles-authz.test",
            FirstName = "First",
            LastName = "Last",
            PasswordHash = "x"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Id, user.Email);
    }

    /// <summary>A role row by name: the existing one, else a new one owned by <paramref name="tenantId"/> (null = global).</summary>
    private async Task<Guid> RoleIdAsync(string name, Guid? tenantId = null, string? category = null, string permissions = "[]")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == name && r.TenantId == tenantId);
        if (role == null)
        {
            role = new Role
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = name,
                TenantId = tenantId,
                Category = category,
                Permissions = permissions
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        return role.Id;
    }

    private async Task GrantAsync(Guid userId, Guid roleId, Guid? tenantId, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = roleId,
            TenantId = tenantId,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@roles-authz.test", roles, tenantClaim));

    /// <summary>A user holding the named role in the given tenant, with a token that carries that role name.</summary>
    private async Task<(Guid UserId, HttpClient Client)> UserWithRoleAsync(
        string roleName, Guid? tenantId, DateTime? expiresAt = null, string? tenantClaim = null)
    {
        var (userId, _) = await CreateUserAsync(roleName.ToLowerInvariant());
        var roleId = await RoleIdAsync(roleName);
        await GrantAsync(userId, roleId, tenantId, expiresAt);
        return (userId, ClientAs(userId, new[] { roleName }, tenantClaim));
    }

    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });

    private HttpClient OrdinaryUser() => ClientAs(Guid.NewGuid(), new[] { "User" });

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<int> RoleCountAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Roles.CountAsync(r => r.Name == name);
    }

    private static object NewRole(string name, Guid? tenantId, string? category = null, string[]? permissions = null) => new
    {
        name,
        description = "test role",
        category,
        tenantId,
        permissions = permissions ?? new[] { "buildings.read" }
    };

    // ----- reading: ordinary users ---------------------------------------------------------------

    [Fact]
    public async Task OrdinaryUser_GetRole_IsForbidden_AndSeesNoHolders()
    {
        var tenant = await CreateTenantAsync();
        var (holderId, holderEmail) = await CreateUserAsync("holder");
        var roleId = await RoleIdAsync($"Cleaner-{Guid.NewGuid():N}", tenant);
        await GrantAsync(holderId, roleId, tenant);

        var response = await OrdinaryUser().GetAsync($"/api/roles/{roleId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(holderEmail, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OrdinaryUser_GetRole_ForUnknownId_IsAlsoForbidden_SoIdsDoNotLeak()
    {
        var response = await OrdinaryUser().GetAsync($"/api/roles/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OrdinaryUser_ListRoles_IsForbidden()
    {
        var response = await OrdinaryUser().GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MemberWithOnlyAPlainTenantRole_IsNotAnAdministrator()
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync($"Member-{Guid.NewGuid():N}", tenant);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/roles")).StatusCode);
    }

    [Fact]
    public async Task ExpiredBuildingOwnerRole_IsNotAnAdministrator()
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("BuildingOwner", tenant, expiresAt: DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/roles")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_ListRoles_IsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ServiceAccountAndDeviceTokens_CannotReadRoles()
    {
        var serviceClient = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("some-app", "roles.read"));
        Assert.Equal(HttpStatusCode.Forbidden, (await serviceClient.GetAsync("/api/roles")).StatusCode);

        var tenant = await CreateTenantAsync();
        var deviceClient = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(Guid.NewGuid(), "dev-1", tenant));
        Assert.Equal(HttpStatusCode.Forbidden, (await deviceClient.GetAsync("/api/roles")).StatusCode);
    }

    // ----- reading: administrators ---------------------------------------------------------------

    [Fact]
    public async Task SuperAdmin_GetRole_SeesAllHolders_IncludingGlobalAssignments()
    {
        var tenant = await CreateTenantAsync();
        var roleId = await RoleIdAsync($"Shared-{Guid.NewGuid():N}");
        var (inTenantId, inTenantEmail) = await CreateUserAsync("in-tenant");
        var (globalId, globalEmail) = await CreateUserAsync("global");
        await GrantAsync(inTenantId, roleId, tenant);
        await GrantAsync(globalId, roleId, null);

        var response = await SuperAdmin().GetAsync($"/api/roles/{roleId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonOf(response);
        Assert.Equal(2, json.GetProperty("userCount").GetInt32());
        var emails = json.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToList();
        Assert.Contains(inTenantEmail, emails);
        Assert.Contains(globalEmail, emails);
        Assert.Equal(0, json.GetProperty("permissions").GetArrayLength());
    }

    [Theory]
    [InlineData("SystemAdmin")]
    [InlineData("SecurityAdmin")]
    public async Task PlatformAdministrators_SeeAllHolders(string platformRole)
    {
        var roleId = await RoleIdAsync($"Shared-{Guid.NewGuid():N}");
        var (holderId, holderEmail) = await CreateUserAsync("holder");
        await GrantAsync(holderId, roleId, null);

        var response = await ClientAs(Guid.NewGuid(), new[] { platformRole }).GetAsync($"/api/roles/{roleId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(holderEmail, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TenantOwner_GetRole_SeesOnlyHoldersInTheirOwnTenants()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var roleId = await RoleIdAsync($"Shared-{Guid.NewGuid():N}");
        var (inAId, inAEmail) = await CreateUserAsync("in-a");
        var (inBId, inBEmail) = await CreateUserAsync("in-b");
        var (globalId, globalEmail) = await CreateUserAsync("global");
        await GrantAsync(inAId, roleId, tenantA);
        await GrantAsync(inBId, roleId, tenantB);
        await GrantAsync(globalId, roleId, null);
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenantA);

        var response = await owner.GetAsync($"/api/roles/{roleId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(inAEmail, body);
        Assert.DoesNotContain(inBEmail, body);
        Assert.DoesNotContain(globalEmail, body);
        Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("userCount").GetInt32());
    }

    [Fact]
    public async Task TenantOwner_CannotSeeWhoHoldsAPlatformRole()
    {
        var tenant = await CreateTenantAsync();
        var superAdminRoleId = await RoleIdAsync("SuperAdmin");
        var (holderId, holderEmail) = await CreateUserAsync("super");
        await GrantAsync(holderId, superAdminRoleId, null);
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenant);

        var response = await owner.GetAsync($"/api/roles/{superAdminRoleId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(holderEmail, body);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(0, json.GetProperty("userCount").GetInt32());
        Assert.Equal(0, json.GetProperty("users").GetArrayLength());
    }

    [Fact]
    public async Task BuildingManager_IsAnAdministrator_OfTheirTenantOnly()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var ownRole = await RoleIdAsync($"Own-{Guid.NewGuid():N}", tenantA);
        var foreignRole = await RoleIdAsync($"Foreign-{Guid.NewGuid():N}", tenantB);
        var (_, manager) = await UserWithRoleAsync("BuildingManager", tenantA);

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/roles/{ownRole}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/roles/{foreignRole}")).StatusCode);
    }

    [Fact]
    public async Task TenantOwner_ListRoles_ShowsGlobalAndOwnTenantRoles_NotOtherTenants()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var globalName = $"Global-{Guid.NewGuid():N}";
        var ownName = $"Own-{Guid.NewGuid():N}";
        var foreignName = $"Foreign-{Guid.NewGuid():N}";
        await RoleIdAsync(globalName);
        await RoleIdAsync(ownName, tenantA);
        await RoleIdAsync(foreignName, tenantB);
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenantA);

        var response = await owner.GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = (await JsonOf(response)).EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Contains(globalName, names);
        Assert.Contains(ownName, names);
        Assert.DoesNotContain(foreignName, names);
    }

    [Fact]
    public async Task SuperAdmin_ListRoles_ShowsEveryRole()
    {
        var tenantB = await CreateTenantAsync();
        var foreignName = $"Foreign-{Guid.NewGuid():N}";
        await RoleIdAsync(foreignName, tenantB);

        var response = await SuperAdmin().GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = (await JsonOf(response)).EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Contains(foreignName, names);
    }

    [Fact]
    public async Task TenantOwner_GetRole_OfAnotherTenant_IsNotFound()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var foreignRole = await RoleIdAsync($"Foreign-{Guid.NewGuid():N}", tenantB);
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenantA);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/roles/{foreignRole}")).StatusCode);
    }

    [Fact]
    public async Task TenantClaim_PinsAnAdministratorToThatTenant()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var roleInB = await RoleIdAsync($"InB-{Guid.NewGuid():N}", tenantB);
        var (userId, _) = await CreateUserAsync("two-tenants");
        var ownerRole = await RoleIdAsync("BuildingOwner");
        await GrantAsync(userId, ownerRole, tenantA);
        await GrantAsync(userId, ownerRole, tenantB);

        var unpinned = ClientAs(userId, new[] { "BuildingOwner" });
        var pinnedToA = ClientAs(userId, new[] { "BuildingOwner" }, tenantA.ToString());

        Assert.Equal(HttpStatusCode.OK, (await unpinned.GetAsync($"/api/roles/{roleInB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pinnedToA.GetAsync($"/api/roles/{roleInB}")).StatusCode);
    }

    [Fact]
    public async Task GetRole_ForAnUnknownId_IsNotFound_ForAnAdministrator()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SuperAdmin().GetAsync($"/api/roles/{Guid.NewGuid()}")).StatusCode);
    }

    // ----- creating: BuildingOwner ---------------------------------------------------------------

    [Fact]
    public async Task BuildingOwner_CanCreateAPlainRole_InTheirOwnTenant()
    {
        var tenant = await CreateTenantAsync();
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenant);
        var name = $"Floor-{Guid.NewGuid():N}";

        var response = await owner.PostAsJsonAsync("/api/roles", NewRole(name, tenant));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await JsonOf(response);
        Assert.Equal(tenant, json.GetProperty("tenantId").GetGuid());
        Assert.False(json.GetProperty("isSystemRole").GetBoolean());

        // and they can read it back
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/roles/{id}")).StatusCode);
    }

    [Fact]
    public async Task BuildingOwner_CanCreateARole_WithANonAppCategory_AndSpecificPermissions()
    {
        var tenant = await CreateTenantAsync();
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenant);

        var response = await owner.PostAsJsonAsync("/api/roles",
            NewRole($"Cat-{Guid.NewGuid():N}", tenant, "Building", new[] { "buildings.read", "devices.control" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task BuildingOwner_CreatingARoleWithoutATenant_IsForbidden_AndNothingIsStored()
    {
        var tenant = await CreateTenantAsync();
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenant);
        var name = $"Global-{Guid.NewGuid():N}";

        var response = await owner.PostAsJsonAsync("/api/roles", NewRole(name, null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await RoleCountAsync(name));
    }

    [Fact]
    public async Task BuildingOwner_CreatingARoleInAnotherTenant_IsForbidden_AndNothingIsStored()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenantA);
        var name = $"Foreign-{Guid.NewGuid():N}";

        var response = await owner.PostAsJsonAsync("/api/roles", NewRole(name, tenantB));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await RoleCountAsync(name));
    }

    [Theory]
    [InlineData("app:test-client")]
    [InlineData("App:Test-Client")]
    [InlineData("  app:test-client")]
    public async Task BuildingOwner_CreatingARoleWithAnAppCategory_IsForbidden_AndNoCatalogAppears(string category)
    {
        var tenant = await CreateTenantAsync();
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenant);
        var name = $"AppRole-{Guid.NewGuid():N}";

        var response = await owner.PostAsJsonAsync("/api/roles", NewRole(name, tenant, category));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await RoleCountAsync(name));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("users.*")]
    [InlineData("*.read")]
    [InlineData("acme:hq:*")]
    public async Task BuildingOwner_CreatingARoleWithWildcardPermissions_IsForbidden(string permission)
    {
        var tenant = await CreateTenantAsync();
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenant);
        var name = $"Wild-{Guid.NewGuid():N}";

        var response = await owner.PostAsJsonAsync("/api/roles", NewRole(name, tenant, null, new[] { "buildings.read", permission }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await RoleCountAsync(name));
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("systemadmin")]
    [InlineData("Admin")]
    public async Task BuildingOwner_CreatingARoleWithAPlatformRoleName_IsForbidden(string name)
    {
        var tenant = await CreateTenantAsync();
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenant);
        var before = await RoleCountAsync(name);

        var response = await owner.PostAsJsonAsync("/api/roles", NewRole(name, tenant));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await RoleCountAsync(name));
    }

    [Fact]
    public async Task BuildingOwnerClaim_WithoutAnActiveBuildingOwnerRow_CannotCreateAnything()
    {
        var tenant = await CreateTenantAsync();
        var noRow = ClientAs(Guid.NewGuid(), new[] { "BuildingOwner" });
        var (_, expired) = await UserWithRoleAsync("BuildingOwner", tenant, expiresAt: DateTime.UtcNow.AddMinutes(-5));
        var (_, globalRowOnly) = await UserWithRoleAsync("BuildingOwner", null);

        foreach (var client in new[] { noRow, expired, globalRowOnly })
        {
            var response = await client.PostAsJsonAsync("/api/roles", NewRole($"X-{Guid.NewGuid():N}", tenant));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task OwnerOfOneTenant_AndManagerOfAnother_CanOnlyCreateInTheOwnedOne()
    {
        var owned = await CreateTenantAsync();
        var managed = await CreateTenantAsync();
        var (userId, _) = await CreateUserAsync("mixed");
        await GrantAsync(userId, await RoleIdAsync("BuildingOwner"), owned);
        await GrantAsync(userId, await RoleIdAsync("BuildingManager"), managed);
        var client = ClientAs(userId, new[] { "BuildingOwner", "BuildingManager" });

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/roles", NewRole($"Own-{Guid.NewGuid():N}", owned))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/roles", NewRole($"Mgd-{Guid.NewGuid():N}", managed))).StatusCode);
    }

    [Fact]
    public async Task TenantClaim_PinsRoleCreationToThatTenant()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var (userId, _) = await CreateUserAsync("two-tenants");
        var ownerRole = await RoleIdAsync("BuildingOwner");
        await GrantAsync(userId, ownerRole, tenantA);
        await GrantAsync(userId, ownerRole, tenantB);
        var pinnedToA = ClientAs(userId, new[] { "BuildingOwner" }, tenantA.ToString());

        Assert.Equal(HttpStatusCode.Created, (await pinnedToA.PostAsJsonAsync("/api/roles", NewRole($"A-{Guid.NewGuid():N}", tenantA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pinnedToA.PostAsJsonAsync("/api/roles", NewRole($"B-{Guid.NewGuid():N}", tenantB))).StatusCode);
    }

    [Fact]
    public async Task OrdinaryUser_CannotCreateARole()
    {
        var tenant = await CreateTenantAsync();

        var response = await OrdinaryUser().PostAsJsonAsync("/api/roles", NewRole($"Nope-{Guid.NewGuid():N}", tenant));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- creating: SuperAdmin ------------------------------------------------------------------

    [Fact]
    public async Task SuperAdmin_StillCreatesGlobalOtherTenantAppCategoryAndWildcardRoles()
    {
        var tenant = await CreateTenantAsync();
        var superAdmin = SuperAdmin();

        var global = await superAdmin.PostAsJsonAsync("/api/roles", NewRole($"G-{Guid.NewGuid():N}", null));
        var otherTenant = await superAdmin.PostAsJsonAsync("/api/roles", NewRole($"T-{Guid.NewGuid():N}", tenant));
        var appCategory = await superAdmin.PostAsJsonAsync("/api/roles", NewRole($"A-{Guid.NewGuid():N}", null, "app:test-client"));
        var wildcard = await superAdmin.PostAsJsonAsync("/api/roles", NewRole($"W-{Guid.NewGuid():N}", tenant, null, new[] { "*" }));

        Assert.Equal(HttpStatusCode.Created, global.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Created, appCategory.StatusCode);
        Assert.Equal(HttpStatusCode.Created, wildcard.StatusCode);
        Assert.Equal("app:test-client", (await JsonOf(appCategory)).GetProperty("category").GetString());
    }

    [Fact]
    public async Task CreatingARole_WithoutAName_IsStillABadRequest()
    {
        var tenant = await CreateTenantAsync();
        var (_, owner) = await UserWithRoleAsync("BuildingOwner", tenant);

        var response = await owner.PostAsJsonAsync("/api/roles", NewRole("  ", tenant));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
