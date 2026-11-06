using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4704: every /api/secrets action needs SuperAdmin or an administrator of the secret's own tenant (global
/// secrets without a tenant: SuperAdmin only), lists only show manageable secrets, and idp-client-secret-* entries
/// cannot be changed through the API while the service keeps working for SocialAuthService internally.
/// </summary>
public class SecretsVaultAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Plain = "super-secret-value-1";

    private readonly IAMTestWebApplicationFactory _factory;

    public SecretsVaultAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

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
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@secrets-authz.test", PasswordHash = "x" };
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
            Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenantId,
            GrantedAt = DateTime.UtcNow, ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedSecretAsync(Guid? tenantId, string? name = null, string type = "Generic")
    {
        using var scope = _factory.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ISecretsVaultService>();
        var entry = await vault.CreateSecretAsync(name ?? $"secret-{Guid.NewGuid():N}", Plain, tenantId, type);
        return entry.Id;
    }

    private async Task<SecretEntry> RowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.SecretEntries.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@secrets-authz.test", roles, tenantClaim));

    private async Task<HttpClient> AdminOfAsync(Guid tenantId, string role = "TenantAdmin", DateTime? expiresAt = null, string? tenantClaim = null)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, role, tenantId, expiresAt);
        return ClientAs(userId, new[] { role }, tenantClaim);
    }

    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });
    private HttpClient OrdinaryUser() => ClientAs(Guid.NewGuid(), new[] { "User" });

    /// <summary>Shaped like DeviceAuthenticationService's token: sub = device GUID, tenant_id, token_type=device, no roles.</summary>
    private static string DeviceToken()
    {
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes("DEVELOPMENT_SECRET_KEY_CHANGE_IN_PRODUCTION_32_CHARS_MIN"));
        var token = new JwtSecurityToken(
            issuer: "https://localhost:5001",
            audience: "iam-api",
            claims: new[]
            {
                new System.Security.Claims.Claim("sub", Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim("tenant_id", Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim("token_type", "device"),
            },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static object CreateBody(Guid? tenantId, string? name = null) => new
    {
        name = name ?? $"new-{Guid.NewGuid():N}",
        value = "created-value",
        tenantId
    };

    private static readonly string[] ActionNames =
    {
        "POST secrets", "GET secrets/{id}", "GET secrets", "PUT secrets/{id}", "POST secrets/rotate/{id}",
        "GET secrets/{id}/history", "DELETE secrets/{id}"
    };

    /// <summary>One call per action (the 8th, GET by tenant filter, is the list call), aimed at a secret of <paramref name="tenantId"/>.</summary>
    private static async Task<HttpStatusCode[]> AllActionsAsync(HttpClient c, Guid tenantId, Guid secretId) => new[]
    {
        (await c.PostAsJsonAsync("/api/secrets", CreateBody(tenantId))).StatusCode,
        (await c.GetAsync($"/api/secrets/{secretId}")).StatusCode,
        (await c.GetAsync($"/api/secrets?tenantId={tenantId}")).StatusCode,
        (await c.PutAsJsonAsync($"/api/secrets/{secretId}", new { description = "changed" })).StatusCode,
        (await c.PostAsJsonAsync($"/api/secrets/rotate/{secretId}", new { newValue = "rotated-value" })).StatusCode,
        (await c.GetAsync($"/api/secrets/{secretId}/history")).StatusCode,
        (await c.DeleteAsync($"/api/secrets/{secretId}")).StatusCode,
    };

    private static void AssertAll(HttpStatusCode[] statuses, HttpStatusCode expected, string[]? names = null)
    {
        names ??= ActionNames;
        for (var i = 0; i < statuses.Length; i++)
            Assert.True(expected == statuses[i], $"{names[i]} returned {statuses[i]}, expected {expected}");
    }

    private static async Task<List<Guid>> ListIdsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    // ----- who is refused ---------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_Gets401() =>
        AssertAll(await AllActionsAsync(_factory.CreateClient(), RootTenantId, Guid.NewGuid()), HttpStatusCode.Unauthorized);

    [Fact]
    public async Task OrdinaryUser_Gets403_OnEveryAction_AndNothingChanges()
    {
        var tenant = await CreateTenantAsync();
        var id = await SeedSecretAsync(tenant);

        AssertAll(await AllActionsAsync(OrdinaryUser(), tenant, id), HttpStatusCode.Forbidden);

        var row = await RowAsync(id);
        Assert.True(row.IsActive);
        Assert.Equal(1, row.Version);
        Assert.Null(row.Description);
    }

    [Fact]
    public async Task MemberWithNonAdminRoleInTheTenant_Gets403()
    {
        var tenant = await CreateTenantAsync();
        var id = await SeedSecretAsync(tenant);
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "User", tenant);

        AssertAll(await AllActionsAsync(ClientAs(userId, new[] { "User" }), tenant, id), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminOfAnotherTenant_Gets403_OnEveryAction_AndNothingChanges()
    {
        var own = await CreateTenantAsync();
        var victim = await CreateTenantAsync();
        var id = await SeedSecretAsync(victim);
        var admin = await AdminOfAsync(own);

        AssertAll(await AllActionsAsync(admin, victim, id), HttpStatusCode.Forbidden);

        var row = await RowAsync(id);
        Assert.True(row.IsActive);
        Assert.Equal(1, row.Version);
        Assert.Null(row.Description);
    }

    [Fact]
    public async Task DeviceToken_And_ServiceAccountToken_Get403()
    {
        var tenant = await CreateTenantAsync();
        var id = await SeedSecretAsync(tenant);

        AssertAll(await AllActionsAsync(ClientWithToken(DeviceToken()), tenant, id), HttpStatusCode.Forbidden);
        AssertAll(await AllActionsAsync(
            ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "secrets:read", "secrets:write")), tenant, id),
            HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ManagerPrivilegeIsChecked_BeforeLookup_SoUnknownIdsAreNotAnOracle()
    {
        var ordinary = OrdinaryUser();

        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync($"/api/secrets/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.DeleteAsync($"/api/secrets/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SuperAdmin().GetAsync($"/api/secrets/{Guid.NewGuid()}")).StatusCode);
    }

    // ----- who is allowed ---------------------------------------------------------------------------

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingOwner")]
    [InlineData("BuildingManager")]
    public async Task AdminOfTheSecretsTenant_CanUseEveryAction(string role)
    {
        var tenant = await CreateTenantAsync();
        var id = await SeedSecretAsync(tenant);
        var admin = await AdminOfAsync(tenant, role);

        var statuses = await AllActionsAsync(admin, tenant, id);

        var expected = new[]
        {
            HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK,
            HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK
        };
        for (var i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == statuses[i], $"{ActionNames[i]} returned {statuses[i]}, expected {expected[i]}");

        var row = await RowAsync(id);
        Assert.Equal("changed", row.Description);
        Assert.Equal(2, row.Version);
        Assert.False(row.IsActive);
    }

    [Fact]
    public async Task SuperAdmin_CanManageAnyTenantsSecrets_AndGlobalOnes()
    {
        var tenant = await CreateTenantAsync();
        var tenantSecret = await SeedSecretAsync(tenant);
        var globalSecret = await SeedSecretAsync(null);
        var su = SuperAdmin();

        var statuses = await AllActionsAsync(su, tenant, tenantSecret);
        Assert.Equal(HttpStatusCode.Created, statuses[0]);
        AssertAll(statuses.Skip(1).ToArray(), HttpStatusCode.OK, ActionNames.Skip(1).ToArray());

        Assert.Equal(HttpStatusCode.OK, (await su.GetAsync($"/api/secrets/{globalSecret}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await su.PutAsJsonAsync($"/api/secrets/{globalSecret}", new { description = "g" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await su.PostAsJsonAsync($"/api/secrets/rotate/{globalSecret}", new { newValue = "v2" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/secrets", CreateBody(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/secrets", CreateBody(tenant))).StatusCode);
    }

    // ----- global secrets (no tenant) -----------------------------------------------------------------

    [Fact]
    public async Task GlobalSecret_IsSuperAdminOnly_ForTenantAdmins()
    {
        var tenant = await CreateTenantAsync();
        var globalSecret = await SeedSecretAsync(null);
        var admin = await AdminOfAsync(tenant);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/secrets/{globalSecret}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/secrets/{globalSecret}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/api/secrets/{globalSecret}", new { description = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/secrets/rotate/{globalSecret}", new { newValue = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync($"/api/secrets/{globalSecret}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/secrets", CreateBody(null))).StatusCode);

        var row = await RowAsync(globalSecret);
        Assert.True(row.IsActive);
        Assert.Equal(1, row.Version);
    }

    // ----- list --------------------------------------------------------------------------------------

    [Fact]
    public async Task List_ForTenantAdmin_OnlyContainsSecretsOfTheirTenants()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var c = await CreateTenantAsync();
        var inA = await SeedSecretAsync(a);
        var inB = await SeedSecretAsync(b);
        var inC = await SeedSecretAsync(c);
        var global = await SeedSecretAsync(null);
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", a);
        await GrantRoleAsync(userId, "BuildingManager", b);
        var client = ClientAs(userId, new[] { "TenantAdmin" });

        var ids = await ListIdsAsync(await client.GetAsync("/api/secrets"));

        Assert.Contains(inA, ids);
        Assert.Contains(inB, ids);
        Assert.DoesNotContain(inC, ids);
        Assert.DoesNotContain(global, ids);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/secrets?tenantId={c}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/secrets?tenantId={a}")).StatusCode);
    }

    [Fact]
    public async Task List_ForSuperAdmin_ContainsEverything()
    {
        var a = await CreateTenantAsync();
        var inA = await SeedSecretAsync(a);
        var global = await SeedSecretAsync(null);

        var ids = await ListIdsAsync(await SuperAdmin().GetAsync("/api/secrets"));

        Assert.Contains(inA, ids);
        Assert.Contains(global, ids);
    }

    // ----- create ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TenantAdmin_CannotCreateForAnotherTenant_OrGlobally_NothingStored()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await AdminOfAsync(own);
        var foreignName = $"foreign-{Guid.NewGuid():N}";
        var globalName = $"global-{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/secrets", CreateBody(other, foreignName))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/secrets", CreateBody(null, globalName))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/secrets", CreateBody(own))).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        Assert.False(await db.SecretEntries.AnyAsync(s => s.Name == foreignName || s.Name == globalName));
    }

    // ----- identity-provider entries ----------------------------------------------------------------------

    [Fact]
    public async Task IdpClientSecrets_CannotBeChangedThroughTheApi_ByAnyone()
    {
        var tenant = await CreateTenantAsync();
        var tenantIdp = await SeedSecretAsync(tenant, $"idp-client-secret-{Guid.NewGuid():N}", "IdentityProviderClientSecret");
        var globalIdp = await SeedSecretAsync(null, $"idp-client-secret-{Guid.NewGuid():N}", "IdentityProviderClientSecret");
        var admin = await AdminOfAsync(tenant);
        var su = SuperAdmin();

        foreach (var (client, id) in new[] { (admin, tenantIdp), (su, tenantIdp), (su, globalIdp) })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/secrets/{id}", new { description = "x", isActive = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/secrets/rotate/{id}", new { newValue = "attacker" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/secrets/{id}")).StatusCode);
        }

        foreach (var id in new[] { tenantIdp, globalIdp })
        {
            var row = await RowAsync(id);
            Assert.True(row.IsActive);
            Assert.Equal(1, row.Version);
            Assert.Null(row.Description);
        }

        // Metadata stays readable for those who may manage the tenant.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/secrets/{tenantIdp}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await su.GetAsync($"/api/secrets/{globalIdp}/history")).StatusCode);
    }

    [Fact]
    public async Task ReservedIdpName_CannotBeCreatedOrRenamedTo()
    {
        var tenant = await CreateTenantAsync();
        var normal = await SeedSecretAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/secrets", CreateBody(tenant, "idp-client-secret-fake"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/secrets/{normal}", new { name = "idp-client-secret-disguised" })).StatusCode);
        Assert.StartsWith("secret-", (await RowAsync(normal)).Name);
    }

    [Fact]
    public async Task VaultService_StillWorksInternallyOnIdpEntries()
    {
        var id = await SeedSecretAsync(null, $"idp-client-secret-{Guid.NewGuid():N}", "IdentityProviderClientSecret");

        using var scope = _factory.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ISecretsVaultService>();
        Assert.Equal(Plain, await vault.GetSecretValueAsync(id));
        await vault.RotateSecretAsync(id, "rotated-internally", "internal", null);
        Assert.Equal("rotated-internally", await vault.GetSecretValueAsync(id));
    }

    // ----- what does not count ------------------------------------------------------------------------------

    [Fact]
    public async Task ExpiredAdminRole_RoleClaimWithoutRow_AndNullTenantRow_DoNotCount()
    {
        var tenant = await CreateTenantAsync();
        var id = await SeedSecretAsync(tenant);

        var expired = await AdminOfAsync(tenant, expiresAt: DateTime.UtcNow.AddMinutes(-5));
        var claimOnly = ClientAs(await CreateUserAsync(), new[] { "TenantAdmin", "BuildingOwner", "BuildingManager" });
        var globalRowUser = await CreateUserAsync();
        await GrantRoleAsync(globalRowUser, "TenantAdmin", null);

        AssertAll(await AllActionsAsync(expired, tenant, id), HttpStatusCode.Forbidden);
        AssertAll(await AllActionsAsync(claimOnly, tenant, id), HttpStatusCode.Forbidden);
        AssertAll(await AllActionsAsync(ClientAs(globalRowUser, new[] { "TenantAdmin" }), tenant, id), HttpStatusCode.Forbidden);
        Assert.True((await RowAsync(id)).IsActive);
    }

    [Fact]
    public async Task TokenWithTenantClaim_OnlyActsInThatTenant()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var inA = await SeedSecretAsync(a);
        var inB = await SeedSecretAsync(b);
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", a);
        await GrantRoleAsync(userId, "TenantAdmin", b);
        var client = ClientAs(userId, new[] { "TenantAdmin" }, a.ToString());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/secrets/{inA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/secrets/{inB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/secrets", CreateBody(b))).StatusCode);
    }
}
