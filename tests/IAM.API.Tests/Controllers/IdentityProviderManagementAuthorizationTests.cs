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
/// Task 4697: POST/PUT/DELETE /api/identity-providers are admin-only.
/// - SuperAdmin/SystemAdmin may manage every provider; a TenantAdmin (UserRole scoped to the tenant)
///   only their own tenant's, and cannot move one to another tenant or make it platform-wide;
/// - everyone else gets 403, whatever the payload or id;
/// - DefaultRoleId must exist, be non-privileged and fit the provider's tenant (400 otherwise);
/// - AutoCreateUsers defaults to false.
/// Reads and the anonymous /public listing keep their existing behaviour.
/// </summary>
public class IdentityProviderManagementAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid TenantAdminRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01");

    private readonly IAMTestWebApplicationFactory _factory;

    public IdentityProviderManagementAuthorizationTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ----- helpers ---------------------------------------------------------------------------

    private async Task<Tenant> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<User> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@idp-authz.test", PasswordHash = "not-used" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A user holding the TenantAdmin role scoped to <paramref name="tenantId"/> (null = unscoped row).</summary>
    private async Task<User> CreateTenantAdminAsync(Guid? tenantId, DateTime? expiresAt = null)
    {
        var user = await CreateUserAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = TenantAdminRoleId,
            TenantId = tenantId,
            ExpiresAt = expiresAt,
            GrantedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A role with exactly <paramref name="name"/> (the privileged-name checks look at the name itself).</summary>
    private async Task<Role> CreateRoleWithExactNameAsync(string name, Guid? tenantId = null, string permissions = "[\"Room.View\"]")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var role = new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, Permissions = permissions };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private async Task<IdentityProvider> CreateProviderAsync(
        Guid? tenantId, Guid? defaultRoleId = null, bool autoCreateUsers = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var provider = new IdentityProvider
        {
            Id = Guid.NewGuid(),
            Name = $"seeded-{Guid.NewGuid():N}",
            DisplayName = "Seeded provider",
            Type = IdentityProviderType.Google,
            TenantId = tenantId,
            ClientId = "seeded-client-id",
            ClientSecret = "seeded-client-secret",
            IsActive = true,
            AutoCreateUsers = autoCreateUsers,
            DefaultRoleId = defaultRoleId
        };
        db.IdentityProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    private async Task<IdentityProvider?> GetProviderAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    /// <summary>
    /// Client authenticated as <paramref name="userId"/>. Default token shape = password login:
    /// NO tenant_id claim. Pass <paramref name="tenantClaim"/> for a tenant-scoped token.
    /// </summary>
    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@idp-authz.test", roles, tenantClaim);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient SuperAdminClient() => ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });

    private HttpClient SystemAdminClient() => ClientAs(Guid.NewGuid(), new[] { "SystemAdmin" });

    private static object CreateBody(Guid? tenantId, Guid? defaultRoleId = null, bool? autoCreateUsers = null) => new
    {
        name = $"idp-{Guid.NewGuid():N}",
        displayName = "Test provider",
        type = "Google",
        tenantId,
        clientId = "client-id",
        clientSecret = "client-secret",
        autoCreateUsers,
        defaultRoleId
    };

    private static object UpdateBody(Guid? tenantId, Guid? defaultRoleId = null, bool? autoCreateUsers = null) => new
    {
        name = "updated-name",
        displayName = "Updated",
        type = "Google",
        tenantId,
        clientId = "client-id-2",
        autoCreateUsers,
        defaultRoleId
    };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ----- POST: who may create ---------------------------------------------------------------

    [Fact]
    public async Task Post_AsPlainUser_IsForbidden_ForTenantAndPlatformWideProviders()
    {
        var tenant = await CreateTenantAsync();
        var client = ClientAs(Guid.NewGuid(), new[] { "User" });

        var tenantResponse = await client.PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id));
        var platformResponse = await client.PostAsJsonAsync("/api/identity-providers", CreateBody(null));

        Assert.Equal(HttpStatusCode.Forbidden, tenantResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, platformResponse.StatusCode);
    }

    [Fact]
    public async Task Post_AsPlainUser_WithPrivilegedDefaultRole_IsForbiddenNotBadRequest()
    {
        // The authorization answer must not depend on (or reveal anything about) the payload.
        var role = await CreateRoleWithExactNameAsync("SuperAdmin");
        var response = await ClientAs(Guid.NewGuid(), new[] { "User" })
            .PostAsJsonAsync("/api/identity-providers", CreateBody(null, role.Id, autoCreateUsers: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_Anonymous_IsUnauthorized()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/identity-providers", CreateBody(null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_AsSuperAdmin_CreatesPlatformWideProvider()
    {
        var response = await SuperAdminClient().PostAsJsonAsync("/api/identity-providers", CreateBody(null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_AsSystemAdmin_CreatesPlatformWideProvider()
    {
        var response = await SystemAdminClient().PostAsJsonAsync("/api/identity-providers", CreateBody(null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_AsTenantAdmin_ForOwnTenant_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(tenant.Id, (await ReadAsync(response)).GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task Post_AsTenantAdmin_ForOwnTenant_WithMatchingTenantClaim_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }, tenantClaim: tenant.Id.ToString())
            .PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_AsTenantAdmin_ForAnotherTenant_IsForbidden()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(own.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PostAsJsonAsync("/api/identity-providers", CreateBody(other.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_AsTenantAdmin_ForPlatformWideProvider_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PostAsJsonAsync("/api/identity-providers", CreateBody(null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_AsTenantAdmin_WithTokenScopedToAnotherTenant_IsForbidden()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(own.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }, tenantClaim: other.Id.ToString())
            .PostAsJsonAsync("/api/identity-providers", CreateBody(own.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_WhenTokenClaimsTenantAdminButNoRoleRowExists_IsForbidden()
    {
        // A role claim alone is not enough for a tenant-scoped decision: the UserRoles row is.
        var tenant = await CreateTenantAsync();
        var user = await CreateUserAsync();

        var response = await ClientAs(user.Id, new[] { "TenantAdmin" })
            .PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_AsTenantAdminWhoseRoleExpired_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id, expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_AsTenantAdminWithUnscopedRoleRow_IsForbidden()
    {
        // A TenantAdmin row with no tenant is not "admin of this tenant".
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenantId: null);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_ForUnknownTenant_AsSuperAdmin_IsBadRequest()
    {
        var response = await SuperAdminClient().PostAsJsonAsync("/api/identity-providers", CreateBody(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ----- POST: defaults ---------------------------------------------------------------------

    [Fact]
    public async Task Post_WithoutAutoCreateUsers_DefaultsToFalse()
    {
        var response = await SuperAdminClient().PostAsJsonAsync("/api/identity-providers", CreateBody(null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.False(body.GetProperty("autoCreateUsers").GetBoolean());
        var stored = await GetProviderAsync(body.GetProperty("id").GetGuid());
        Assert.False(stored!.AutoCreateUsers);
    }

    [Fact]
    public async Task Post_WithExplicitAutoCreateUsersTrue_IsHonoured()
    {
        var response = await SuperAdminClient()
            .PostAsJsonAsync("/api/identity-providers", CreateBody(null, autoCreateUsers: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("autoCreateUsers").GetBoolean());
    }

    // ----- POST: default role validation ------------------------------------------------------

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    [InlineData("SecurityAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("ComplianceOfficer")]
    [InlineData("EmergencyAccess")]
    [InlineData("BuildingOwner")]
    [InlineData("OrganizationOwner")]
    [InlineData("Admin")]
    [InlineData("admin")]
    [InlineData("Contoso-Admin")]
    public async Task Post_WithPrivilegedDefaultRole_IsBadRequest_EvenForSuperAdmin(string roleName)
    {
        var role = await CreateRoleWithExactNameAsync(roleName);

        var response = await SuperAdminClient()
            .PostAsJsonAsync("/api/identity-providers", CreateBody(null, role.Id, autoCreateUsers: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithWildcardPermissionDefaultRole_IsBadRequest()
    {
        var role = await CreateRoleWithExactNameAsync("PowerUser", permissions: "[\"*\"]");

        var response = await SuperAdminClient()
            .PostAsJsonAsync("/api/identity-providers", CreateBody(null, role.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithUnknownDefaultRole_IsBadRequest()
    {
        var response = await SuperAdminClient()
            .PostAsJsonAsync("/api/identity-providers", CreateBody(null, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithPrivilegedDefaultRole_AsTenantAdmin_IsBadRequest()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);
        var superAdminRole = await CreateRoleWithExactNameAsync("SuperAdmin");

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id, superAdminRole.Id, autoCreateUsers: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithPlainGlobalDefaultRole_Succeeds_ForTenantAndPlatformProviders()
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleWithExactNameAsync("Resident-4697", tenantId: null);

        var platform = await SuperAdminClient()
            .PostAsJsonAsync("/api/identity-providers", CreateBody(null, role.Id, autoCreateUsers: true));
        var scoped = await SuperAdminClient()
            .PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id, role.Id, autoCreateUsers: true));

        Assert.Equal(HttpStatusCode.Created, platform.StatusCode);
        Assert.Equal(HttpStatusCode.Created, scoped.StatusCode);
        Assert.Equal(role.Id, (await ReadAsync(scoped)).GetProperty("defaultRoleId").GetGuid());
    }

    [Fact]
    public async Task Post_WithDefaultRoleOfAnotherTenant_IsBadRequest()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var foreignRole = await CreateRoleWithExactNameAsync("Member-4697", tenantId: otherTenant.Id);

        var response = await SuperAdminClient()
            .PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id, foreignRole.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithDefaultRoleOfTheProvidersOwnTenant_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var ownRole = await CreateRoleWithExactNameAsync("Member-4697", tenantId: tenant.Id);

        var response = await SuperAdminClient()
            .PostAsJsonAsync("/api/identity-providers", CreateBody(tenant.Id, ownRole.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ----- PUT --------------------------------------------------------------------------------

    [Fact]
    public async Task Put_AsPlainUser_IsForbidden_AndProviderIsUnchanged()
    {
        var tenant = await CreateTenantAsync();
        var provider = await CreateProviderAsync(tenant.Id);
        var superAdminRole = await CreateRoleWithExactNameAsync("SuperAdmin");

        // The attack from the finding: enable auto-create with a SuperAdmin default role.
        var response = await ClientAs(Guid.NewGuid(), new[] { "User" })
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(tenant.Id, superAdminRole.Id, autoCreateUsers: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var stored = await GetProviderAsync(provider.Id);
        Assert.Null(stored!.DefaultRoleId);
        Assert.False(stored.AutoCreateUsers);
        Assert.Equal(provider.Name, stored.Name);
    }

    [Fact]
    public async Task Put_AsPlainUser_ForUnknownId_IsForbiddenNotNotFound()
    {
        var response = await ClientAs(Guid.NewGuid(), new[] { "User" })
            .PutAsJsonAsync($"/api/identity-providers/{Guid.NewGuid()}", UpdateBody(null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_Anonymous_IsUnauthorized()
    {
        var response = await _factory.CreateClient()
            .PutAsJsonAsync($"/api/identity-providers/{Guid.NewGuid()}", UpdateBody(null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_AsTenantAdmin_ForOwnTenantProvider_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);
        var provider = await CreateProviderAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(tenant.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("updated-name", (await GetProviderAsync(provider.Id))!.Name);
    }

    [Fact]
    public async Task Put_AsTenantAdmin_ForAnotherTenantsProvider_IsForbidden()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(own.Id);
        var provider = await CreateProviderAsync(other.Id);

        // Whether the body names the victim tenant or the attacker's own tenant: authority is judged
        // on the STORED tenant.
        var asVictimTenant = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(other.Id));
        var claimingOwnTenant = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(own.Id));

        Assert.Equal(HttpStatusCode.Forbidden, asVictimTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, claimingOwnTenant.StatusCode);
        var stored = await GetProviderAsync(provider.Id);
        Assert.Equal(other.Id, stored!.TenantId);
        Assert.Equal(provider.Name, stored.Name);
    }

    [Fact]
    public async Task Put_AsTenantAdmin_ForPlatformWideProvider_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);
        var provider = await CreateProviderAsync(null);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(tenant.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null((await GetProviderAsync(provider.Id))!.TenantId);
    }

    [Fact]
    public async Task Put_AsTenantAdmin_MovingProviderToAnotherTenant_IsForbidden()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(own.Id);
        var provider = await CreateProviderAsync(own.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(other.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(own.Id, (await GetProviderAsync(provider.Id))!.TenantId);
    }

    [Fact]
    public async Task Put_AsTenantAdmin_MakingProviderPlatformWide_IsForbidden()
    {
        // Including by simply omitting tenantId from the PUT body.
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);
        var provider = await CreateProviderAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(tenant.Id, (await GetProviderAsync(provider.Id))!.TenantId);
    }

    [Fact]
    public async Task Put_AsSuperAdmin_CanMoveProviderToAnotherTenant()
    {
        var from = await CreateTenantAsync();
        var to = await CreateTenantAsync();
        var provider = await CreateProviderAsync(from.Id);

        var response = await SuperAdminClient()
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(to.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(to.Id, (await GetProviderAsync(provider.Id))!.TenantId);
    }

    [Fact]
    public async Task Put_AsSystemAdmin_CanUpdatePlatformWideProvider()
    {
        var provider = await CreateProviderAsync(null);

        var response = await SystemAdminClient()
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithPrivilegedDefaultRole_IsBadRequest_AndProviderIsUnchanged()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);
        var provider = await CreateProviderAsync(tenant.Id);
        var superAdminRole = await CreateRoleWithExactNameAsync("SuperAdmin");

        var asTenantAdmin = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(tenant.Id, superAdminRole.Id, autoCreateUsers: true));
        var asSuperAdmin = await SuperAdminClient()
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(tenant.Id, superAdminRole.Id, autoCreateUsers: true));

        Assert.Equal(HttpStatusCode.BadRequest, asTenantAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, asSuperAdmin.StatusCode);
        var stored = await GetProviderAsync(provider.Id);
        Assert.Null(stored!.DefaultRoleId);
        Assert.False(stored.AutoCreateUsers);
    }

    [Fact]
    public async Task Put_WithUnknownDefaultRole_IsBadRequest()
    {
        var provider = await CreateProviderAsync(null);

        var response = await SuperAdminClient()
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(null, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithPlainDefaultRole_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);
        var provider = await CreateProviderAsync(tenant.Id);
        var role = await CreateRoleWithExactNameAsync("Resident-4697");

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(tenant.Id, role.Id, autoCreateUsers: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await GetProviderAsync(provider.Id);
        Assert.Equal(role.Id, stored!.DefaultRoleId);
        Assert.True(stored.AutoCreateUsers);
    }

    [Fact]
    public async Task Put_OmittingAutoCreateUsers_TurnsItOff()
    {
        // PUT replaces the provider; the omitted flag takes the safe default instead of silently becoming true.
        var provider = await CreateProviderAsync(null, autoCreateUsers: true);

        var response = await SuperAdminClient()
            .PutAsJsonAsync($"/api/identity-providers/{provider.Id}", UpdateBody(null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await GetProviderAsync(provider.Id))!.AutoCreateUsers);
    }

    [Fact]
    public async Task Put_ForUnknownId_AsSuperAdmin_IsNotFound()
    {
        var response = await SuperAdminClient()
            .PutAsJsonAsync($"/api/identity-providers/{Guid.NewGuid()}", UpdateBody(null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- DELETE -----------------------------------------------------------------------------

    [Fact]
    public async Task Delete_AsPlainUser_IsForbidden_AndProviderSurvives()
    {
        var tenant = await CreateTenantAsync();
        var provider = await CreateProviderAsync(tenant.Id);

        var response = await ClientAs(Guid.NewGuid(), new[] { "User" })
            .DeleteAsync($"/api/identity-providers/{provider.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await GetProviderAsync(provider.Id));
    }

    [Fact]
    public async Task Delete_AsPlainUser_ForUnknownId_IsForbiddenNotNotFound()
    {
        var response = await ClientAs(Guid.NewGuid(), new[] { "User" })
            .DeleteAsync($"/api/identity-providers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Anonymous_IsUnauthorized()
    {
        var response = await _factory.CreateClient().DeleteAsync($"/api/identity-providers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_AsTenantAdmin_OwnTenantProvider_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);
        var provider = await CreateProviderAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .DeleteAsync($"/api/identity-providers/{provider.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await GetProviderAsync(provider.Id));
    }

    [Fact]
    public async Task Delete_AsTenantAdmin_AnotherTenantsOrPlatformProvider_IsForbidden()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(own.Id);
        var otherTenantProvider = await CreateProviderAsync(other.Id);
        var platformProvider = await CreateProviderAsync(null);
        var client = ClientAs(admin.Id, new[] { "TenantAdmin" });

        var otherResponse = await client.DeleteAsync($"/api/identity-providers/{otherTenantProvider.Id}");
        var platformResponse = await client.DeleteAsync($"/api/identity-providers/{platformProvider.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, otherResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, platformResponse.StatusCode);
        Assert.NotNull(await GetProviderAsync(otherTenantProvider.Id));
        Assert.NotNull(await GetProviderAsync(platformProvider.Id));
    }

    [Fact]
    public async Task Delete_AsSuperAdmin_AndSystemAdmin_Succeed()
    {
        var tenant = await CreateTenantAsync();
        var first = await CreateProviderAsync(tenant.Id);
        var second = await CreateProviderAsync(null);

        var asSuperAdmin = await SuperAdminClient().DeleteAsync($"/api/identity-providers/{first.Id}");
        var asSystemAdmin = await SystemAdminClient().DeleteAsync($"/api/identity-providers/{second.Id}");

        Assert.Equal(HttpStatusCode.OK, asSuperAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asSystemAdmin.StatusCode);
        Assert.Null(await GetProviderAsync(first.Id));
        Assert.Null(await GetProviderAsync(second.Id));
    }

    [Fact]
    public async Task Delete_ForUnknownId_AsSuperAdmin_IsNotFound()
    {
        var response = await SuperAdminClient().DeleteAsync($"/api/identity-providers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- anonymous + read paths keep working ------------------------------------------------

    [Fact]
    public async Task PublicListing_StillWorksAnonymously_AndShowsTheNewProvider()
    {
        var tenant = await CreateTenantAsync();
        var provider = await CreateProviderAsync(tenant.Id);

        var response = await _factory.CreateClient().GetAsync($"/api/identity-providers/public?tenantId={tenant.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var providers = (await ReadAsync(response)).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(provider.Id, providers);
    }

    [Fact]
    public async Task AdminListing_IsForbiddenForAPlainUser()
    {
        // Task 4739: reading provider configuration is admin-only (was open to any authenticated user).
        var response = await ClientAs(Guid.NewGuid(), new[] { "User" }).GetAsync("/api/identity-providers");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
