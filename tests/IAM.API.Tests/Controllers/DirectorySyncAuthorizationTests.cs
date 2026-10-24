using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.API.Tests.Services;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4698: every /api/directory-sync action needs SuperAdmin or an active BuildingOwner UserRole for the
/// configuration's tenant (403 otherwise, before any lookup); group mappings can only name tenant roles (400); the
/// LDAP URL must be ldaps:// and public outside SuperAdmin; the bind password is stored encrypted and never returned.
/// </summary>
public class DirectorySyncAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    public DirectorySyncAuthorizationTests(IAMTestWebApplicationFactory baseFactory)
    {
        // Same in-memory database as the base factory, with DNS and the LDAP server faked.
        _factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostResolver>();
            services.AddSingleton<IHostResolver, FakeHostResolver>();
            services.RemoveAll<ILdapDirectoryClient>();
            services.AddScoped<ILdapDirectoryClient, FakeLdapDirectoryClient>();
        }));
    }

    private const string PublicUrl = "ldaps://" + FakeHostResolver.PublicHost + ":636";
    private static readonly Guid OwnerRoleId = Guid.NewGuid();
    private static readonly Guid ManagerRoleId = Guid.NewGuid();
    private static readonly SemaphoreSlim RoleLock = new(1, 1);

    // ----- helpers ---------------------------------------------------------------------------

    private IAMDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAMDbContext>();

    private async Task EnsureRolesAsync()
    {
        await RoleLock.WaitAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);
            if (!await db.Roles.AnyAsync(r => r.Id == OwnerRoleId))
            {
                db.Roles.Add(new Role { Id = OwnerRoleId, Name = "BuildingOwner", Permissions = "[]" });
                db.Roles.Add(new Role { Id = ManagerRoleId, Name = "BuildingManager", Permissions = "[]" });
                await db.SaveChangesAsync();
            }
        }
        finally { RoleLock.Release(); }
    }

    private async Task<Tenant> CreateTenantAsync()
    {
        await EnsureRolesAsync();
        using var scope = _factory.Services.CreateScope();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        Db(scope).Tenants.Add(tenant);
        await Db(scope).SaveChangesAsync();
        return tenant;
    }

    private async Task<User> CreateUserWithRoleAsync(Guid roleId, Guid? tenantId, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@dirsync.test", PasswordHash = "x", IsActive = true };
        Db(scope).Users.Add(user);
        Db(scope).UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = roleId, TenantId = tenantId, ExpiresAt = expiresAt, GrantedAt = DateTime.UtcNow });
        await Db(scope).SaveChangesAsync();
        return user;
    }

    private async Task<Role> CreateRoleAsync(string name, Guid? tenantId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var role = new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, Permissions = "[]" };
        Db(scope).Roles.Add(role);
        await Db(scope).SaveChangesAsync();
        return role;
    }

    private async Task<DirectorySyncConfig> SeedConfigAsync(Guid tenantId, string bindPassword = "")
    {
        using var scope = _factory.Services.CreateScope();
        var config = new DirectorySyncConfig
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "Corp", LdapUrl = PublicUrl, BindDn = "cn=bind",
            BindPassword = bindPassword, SearchBase = "dc=x", AttributeMapping = "{}", GroupToRoleMapping = "{}"
        };
        Db(scope).DirectorySyncConfigs.Add(config);
        await Db(scope).SaveChangesAsync();
        return config;
    }

    private async Task<DirectorySyncConfig?> GetConfigFromDbAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await Db(scope).DirectorySyncConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    private HttpClient ClientAs(Guid userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@dirsync.test", roles, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), "SuperAdmin");

    private async Task<HttpClient> OwnerOfAsync(Tenant tenant) =>
        ClientAs((await CreateUserWithRoleAsync(OwnerRoleId, tenant.Id)).Id, "BuildingOwner");

    private static object CreateBody(Guid tenantId, string url = PublicUrl, string? password = "Sup3r-Secret-Pw!", object? mapping = null) => new
    {
        tenantId, name = "Corp AD", ldapUrl = url, bindDn = "cn=bind,dc=x", bindPassword = password,
        searchBase = "dc=x", groupToRoleMapping = mapping
    };

    /// <summary>All eight actions against one configuration; the status each returns for this caller.</summary>
    private async Task<Dictionary<string, HttpStatusCode>> HitEveryActionAsync(HttpClient client, DirectorySyncConfig config)
    {
        var id = config.Id;
        var result = new Dictionary<string, HttpStatusCode>
        {
            ["list"] = (await client.GetAsync($"/api/directory-sync/configs?tenantId={config.TenantId}")).StatusCode,
            ["get"] = (await client.GetAsync($"/api/directory-sync/configs/{id}")).StatusCode,
            ["create"] = (await client.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(config.TenantId))).StatusCode,
            ["update"] = (await client.PutAsJsonAsync($"/api/directory-sync/configs/{id}", new { name = "Renamed", ldapUrl = PublicUrl, searchBase = "dc=x" })).StatusCode,
            ["test"] = (await client.PostAsync($"/api/directory-sync/configs/{id}/test-connection", null)).StatusCode,
            ["sync"] = (await client.PostAsync($"/api/directory-sync/configs/{id}/sync", null)).StatusCode,
            ["logs"] = (await client.GetAsync($"/api/directory-sync/configs/{id}/logs")).StatusCode,
            ["delete"] = (await client.DeleteAsync($"/api/directory-sync/configs/{id}")).StatusCode,
        };
        return result;
    }

    private static void AssertAll(Dictionary<string, HttpStatusCode> results, HttpStatusCode expected) =>
        Assert.All(results, kv => Assert.True(kv.Value == expected, $"{kv.Key}: expected {expected}, got {kv.Value}"));

    // ----- who may do what ---------------------------------------------------------------------

    [Fact]
    public async Task EveryAction_Anonymous_IsUnauthorized()
    {
        var tenant = await CreateTenantAsync();
        var config = await SeedConfigAsync(tenant.Id);

        AssertAll(await HitEveryActionAsync(_factory.CreateClient(), config), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EveryAction_PlainUser_IsForbidden_AndNothingChanges()
    {
        var tenant = await CreateTenantAsync();
        var config = await SeedConfigAsync(tenant.Id);

        AssertAll(await HitEveryActionAsync(ClientAs(Guid.NewGuid(), "User"), config), HttpStatusCode.Forbidden);

        var stored = await GetConfigFromDbAsync(config.Id);
        Assert.Equal("Corp", stored!.Name);
    }

    [Fact]
    public async Task EveryAction_ManagerOfTheTenant_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var config = await SeedConfigAsync(tenant.Id);
        var manager = ClientAs((await CreateUserWithRoleAsync(ManagerRoleId, tenant.Id)).Id, "BuildingManager");

        AssertAll(await HitEveryActionAsync(manager, config), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EveryAction_OwnerOfAnotherTenant_IsForbidden_AndTheConfigIsUntouched()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var config = await SeedConfigAsync(b.Id);

        AssertAll(await HitEveryActionAsync(await OwnerOfAsync(a), config), HttpStatusCode.Forbidden);

        Assert.NotNull(await GetConfigFromDbAsync(config.Id));
        Assert.Equal("Corp", (await GetConfigFromDbAsync(config.Id))!.Name);
    }

    [Fact]
    public async Task EveryAction_RoleClaimOnly_UnscopedOrExpiredOwnerRow_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var config = await SeedConfigAsync(tenant.Id);
        var claimOnly = ClientAs(Guid.NewGuid(), "BuildingOwner");
        var unscoped = ClientAs((await CreateUserWithRoleAsync(OwnerRoleId, null)).Id, "BuildingOwner");
        var expired = ClientAs((await CreateUserWithRoleAsync(OwnerRoleId, tenant.Id, DateTime.UtcNow.AddMinutes(-5))).Id, "BuildingOwner");

        foreach (var client in new[] { claimOnly, unscoped, expired })
            AssertAll(await HitEveryActionAsync(client, config), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task IdRoutes_UnknownConfig_IsForbiddenForPlainUsers_NotFoundForOwnersAndSuperAdmin()
    {
        var tenant = await CreateTenantAsync();
        var unknown = $"/api/directory-sync/configs/{Guid.NewGuid()}";

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(Guid.NewGuid(), "User").GetAsync(unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await OwnerOfAsync(tenant)).GetAsync(unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SuperAdmin().GetAsync(unknown)).StatusCode);
    }

    [Fact]
    public async Task Create_ForAnotherTenantsId_IsForbidden_BeforeAnyValidation()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var response = await (await OwnerOfAsync(a)).PostAsJsonAsync("/api/directory-sync/configs", CreateBody(b.Id, url: "ldap://not-even-valid"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EveryAction_OwnOwner_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var config = await SeedConfigAsync(tenant.Id);
        var owner = await OwnerOfAsync(tenant);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/directory-sync/configs?tenantId={tenant.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/directory-sync/configs/{config.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/directory-sync/configs/{config.Id}", new { name = "Renamed", ldapUrl = PublicUrl, searchBase = "dc=x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/directory-sync/configs/{config.Id}/test-connection", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/directory-sync/configs/{config.Id}/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/directory-sync/configs/{config.Id}/logs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/directory-sync/configs/{config.Id}")).StatusCode);
    }

    [Fact]
    public async Task EveryAction_SuperAdmin_SucceedsOnAnyTenant()
    {
        var tenant = await CreateTenantAsync();
        var config = await SeedConfigAsync(tenant.Id);
        var admin = SuperAdmin();

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/directory-sync/configs?tenantId={tenant.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/directory-sync/configs/{config.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/directory-sync/configs/{config.Id}/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/directory-sync/configs/{config.Id}")).StatusCode);
    }

    // ----- validation --------------------------------------------------------------------------

    [Fact]
    public async Task Create_RefusesPlainLdapUrl_AndPrivateHosts_ForOwners_ButSuperAdminMayUseAnInternalHost()
    {
        var tenant = await CreateTenantAsync();
        var owner = await OwnerOfAsync(tenant);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id, url: "ldap://" + FakeHostResolver.PublicHost))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id, url: "ldaps://" + FakeHostResolver.PrivateHost))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id, url: "ldaps://127.0.0.1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SuperAdmin().PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id, url: "ldap://" + FakeHostResolver.PublicHost))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SuperAdmin().PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id, url: "ldaps://" + FakeHostResolver.PrivateHost))).StatusCode);
    }

    [Fact]
    public async Task Update_RefusesPlainLdapUrl()
    {
        var tenant = await CreateTenantAsync();
        var config = await SeedConfigAsync(tenant.Id);

        var response = await (await OwnerOfAsync(tenant)).PutAsJsonAsync($"/api/directory-sync/configs/{config.Id}",
            new { name = "x", ldapUrl = "ldap://" + FakeHostResolver.PublicHost, searchBase = "dc=x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PublicUrl, (await GetConfigFromDbAsync(config.Id))!.LdapUrl);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    [InlineData("SecurityAdmin")]
    [InlineData("ComplianceOfficer")]
    [InlineData("EmergencyAccess")]
    [InlineData("Admin")]
    public async Task Create_MappingAGroupToAPlatformRole_IsBadRequest(string roleName)
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync(roleName);
        var customCopy = await CreateRoleAsync(roleName, tenant.Id);

        foreach (var id in new[] { role.Id, customCopy.Id })
        {
            var asOwner = await (await OwnerOfAsync(tenant)).PostAsJsonAsync("/api/directory-sync/configs",
                CreateBody(tenant.Id, mapping: new Dictionary<string, string> { ["cn=admins,dc=x"] = id.ToString() }));
            var asSuperAdmin = await SuperAdmin().PostAsJsonAsync("/api/directory-sync/configs",
                CreateBody(tenant.Id, mapping: new Dictionary<string, string> { ["cn=admins,dc=x"] = id.ToString() }));

            Assert.Equal(HttpStatusCode.BadRequest, asOwner.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, asSuperAdmin.StatusCode);
        }
    }

    [Fact]
    public async Task Update_MappingAGroupToSuperAdmin_IsBadRequest_AndTheMappingIsUnchanged()
    {
        var tenant = await CreateTenantAsync();
        var config = await SeedConfigAsync(tenant.Id);
        var superAdmin = await CreateRoleAsync("SuperAdmin");

        var response = await (await OwnerOfAsync(tenant)).PutAsJsonAsync($"/api/directory-sync/configs/{config.Id}", new
        {
            name = "x", ldapUrl = PublicUrl, searchBase = "dc=x",
            groupToRoleMapping = new Dictionary<string, string> { ["cn=admins"] = superAdmin.Id.ToString() }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{}", (await GetConfigFromDbAsync(config.Id))!.GroupToRoleMapping);
    }

    [Fact]
    public async Task Create_MappingToATenantRole_Works_ButNotToAnotherTenantsRole()
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var own = await CreateRoleAsync("Front Desk", tenant.Id);
        var foreign = await CreateRoleAsync("Front Desk", other.Id);
        var owner = await OwnerOfAsync(tenant);

        var ok = await owner.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id, mapping: new Dictionary<string, string> { ["cn=desk"] = own.Id.ToString() }));
        var bad = await owner.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id, mapping: new Dictionary<string, string> { ["cn=desk"] = foreign.Id.ToString() }));

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ----- bind password -----------------------------------------------------------------------

    [Fact]
    public async Task BindPassword_IsStoredEncrypted_AndNeverReturned()
    {
        const string secret = "Sup3r-Secret-Pw-4698!";
        var tenant = await CreateTenantAsync();
        var owner = await OwnerOfAsync(tenant);

        var create = await owner.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id, password: secret));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var stored = await GetConfigFromDbAsync(id);
        Assert.StartsWith("enc:", stored!.BindPassword);
        Assert.DoesNotContain(secret, stored.BindPassword);
        using (var scope = _factory.Services.CreateScope())
            Assert.All(await Db(scope).SecretEntries.AsNoTracking().ToListAsync(), s => Assert.DoesNotContain(secret, s.EncryptedValue));

        foreach (var body in new[]
        {
            await create.Content.ReadAsStringAsync(),
            await (await owner.GetAsync($"/api/directory-sync/configs/{id}")).Content.ReadAsStringAsync(),
            await (await owner.GetAsync($"/api/directory-sync/configs?tenantId={tenant.Id}")).Content.ReadAsStringAsync(),
        })
        {
            Assert.DoesNotContain(secret, body);
            Assert.DoesNotContain("bindPassword", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("enc:", body);
        }
    }

    [Fact]
    public async Task BindPassword_UpdateKeepsItEncrypted_AndAnEmptyUpdateKeepsTheOldOne()
    {
        var tenant = await CreateTenantAsync();
        var owner = await OwnerOfAsync(tenant);
        var created = await (await owner.PostAsJsonAsync("/api/directory-sync/configs", CreateBody(tenant.Id, password: "first-pw-4698"))).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var firstRef = (await GetConfigFromDbAsync(id))!.BindPassword;

        await owner.PutAsJsonAsync($"/api/directory-sync/configs/{id}", new { name = "Renamed", ldapUrl = PublicUrl, searchBase = "dc=x" });
        Assert.Equal(firstRef, (await GetConfigFromDbAsync(id))!.BindPassword);

        await owner.PutAsJsonAsync($"/api/directory-sync/configs/{id}", new { name = "Renamed", ldapUrl = PublicUrl, searchBase = "dc=x", bindPassword = "second-pw-4698" });
        var secondRef = (await GetConfigFromDbAsync(id))!.BindPassword;
        Assert.StartsWith("enc:", secondRef);
        Assert.NotEqual(firstRef, secondRef);
        Assert.DoesNotContain("second-pw-4698", secondRef);
    }
}
