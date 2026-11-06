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
/// Task 4738: GET/PUT /api/organization-settings/{tenantId} are limited to SuperAdmin/SystemAdmin and
/// the tenant's own TenantAdmin (an active UserRole scoped to that tenant); the answer for everyone else
/// is 403 and comes before any tenant or settings lookup. PUT also validates DefaultRoleId: it must
/// exist, be non-privileged, and be global or owned by the tenant (400 otherwise, nothing changed).
/// </summary>
public class OrganizationSettingsAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid TenantAdminRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01");

    private readonly IAMTestWebApplicationFactory _factory;

    public OrganizationSettingsAuthorizationTests(IAMTestWebApplicationFactory factory)
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
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@org-settings-authz.test", PasswordHash = "not-used" };
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

    private async Task<Role> CreateRoleAsync(string name, Guid? tenantId = null, string permissions = "[\"Room.View\"]")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var role = new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, Permissions = permissions };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private async Task<OrganizationSettings> SeedSettingsAsync(
        Guid tenantId, Guid? defaultRoleId = null, string? welcomeMessage = "original", int maxMembers = 5)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var settings = new OrganizationSettings
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DefaultRoleId = defaultRoleId,
            WelcomeMessage = welcomeMessage,
            MaxMembers = maxMembers,
            RequireMfa = false
        };
        db.Set<OrganizationSettings>().Add(settings);
        await db.SaveChangesAsync();
        return settings;
    }

    private async Task<OrganizationSettings?> GetSettingsFromDbAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Set<OrganizationSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId);
    }

    /// <summary>
    /// Client authenticated as <paramref name="userId"/>. Default token shape = password login:
    /// NO tenant_id claim. Pass <paramref name="tenantClaim"/> for a tenant-scoped token.
    /// </summary>
    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@org-settings-authz.test", roles, tenantClaim);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient SuperAdminClient() => ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });

    private HttpClient SystemAdminClient() => ClientAs(Guid.NewGuid(), new[] { "SystemAdmin" });

    private static string Url(Guid tenantId) => $"/api/organization-settings/{tenantId}";

    private static object PutBody(Guid? defaultRoleId = null, string? welcomeMessage = null, int? maxMembers = null) => new
    {
        allowedEmailDomains = new[] { "acme.test" },
        requireMfa = true,
        defaultRoleId,
        maxMembers,
        welcomeMessage
    };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ----- GET: who may read ------------------------------------------------------------------

    [Fact]
    public async Task Get_Anonymous_IsUnauthorized()
    {
        var tenant = await CreateTenantAsync();

        var response = await _factory.CreateClient().GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsPlainUser_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        await SeedSettingsAsync(tenant.Id);

        var response = await ClientAs(Guid.NewGuid(), new[] { "User" }).GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsPlainUser_ForUnknownTenant_IsForbiddenNotNotFound()
    {
        var response = await ClientAs(Guid.NewGuid(), new[] { "User" }).GetAsync(Url(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsPlainUser_DoesNotLeakSettingsInTheBody()
    {
        var tenant = await CreateTenantAsync();
        await SeedSettingsAsync(tenant.Id, welcomeMessage: "secret welcome text");

        var response = await ClientAs(Guid.NewGuid(), new[] { "User" }).GetAsync(Url(tenant.Id));

        Assert.DoesNotContain("secret welcome text", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_AsAdminOfAnotherTenant_IsForbidden()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(own.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }).GetAsync(Url(other.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsTenantAdmin_WithTokenScopedToAnotherTenant_IsForbidden()
    {
        // The user does administer the tenant, but the token was issued for another one: fail closed.
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }, tenantClaim: other.Id.ToString())
            .GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithRoleClaimOnly_AndNoUserRoleRow_IsForbidden()
    {
        // A "TenantAdmin" role claim is a global name; only a UserRole row scoped to the tenant counts.
        var tenant = await CreateTenantAsync();
        var user = await CreateUserAsync();

        var response = await ClientAs(user.Id, new[] { "TenantAdmin" }).GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsTenantAdmin_WithUnscopedUserRoleRow_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenantId: null);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }).GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsTenantAdmin_WithExpiredRole_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id, expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }).GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsOwnTenantAdmin_ReturnsSettings()
    {
        var tenant = await CreateTenantAsync();
        await SeedSettingsAsync(tenant.Id, welcomeMessage: "hello team");
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }).GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello team", (await ReadAsync(response)).GetProperty("welcomeMessage").GetString());
    }

    [Fact]
    public async Task Get_AsOwnTenantAdmin_WithMatchingTenantClaim_ReturnsSettings()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }, tenantClaim: tenant.Id.ToString())
            .GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsOwnTenantAdmin_WithNeverExpiringFutureExpiry_ReturnsSettings()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id, expiresAt: DateTime.UtcNow.AddDays(1));

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }).GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsSuperAdmin_ReturnsSettingsOfAnyTenant()
    {
        var tenant = await CreateTenantAsync();
        await SeedSettingsAsync(tenant.Id, welcomeMessage: "visible to super admin");

        var response = await SuperAdminClient().GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("visible to super admin", (await ReadAsync(response)).GetProperty("welcomeMessage").GetString());
    }

    [Fact]
    public async Task Get_AsSuperAdmin_WithTokenScopedToAnotherTenant_ReturnsSettings()
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();

        var response = await ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" }, tenantClaim: other.Id.ToString())
            .GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsSystemAdmin_ReturnsSettingsOfAnyTenant()
    {
        var tenant = await CreateTenantAsync();

        var response = await SystemAdminClient().GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsSuperAdmin_WithNoStoredSettings_ReturnsDefaults()
    {
        var tenant = await CreateTenantAsync();

        var response = await SuperAdminClient().GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await ReadAsync(response)).GetProperty("maxMembers").GetInt32());
    }

    // ----- PUT: who may write -----------------------------------------------------------------

    [Fact]
    public async Task Put_Anonymous_IsUnauthorized()
    {
        var tenant = await CreateTenantAsync();

        var response = await _factory.CreateClient().PutAsJsonAsync(Url(tenant.Id), PutBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_AsPlainUser_IsForbidden_AndChangesNothing()
    {
        var tenant = await CreateTenantAsync();
        await SeedSettingsAsync(tenant.Id, welcomeMessage: "original", maxMembers: 5);

        var response = await ClientAs(Guid.NewGuid(), new[] { "User" })
            .PutAsJsonAsync(Url(tenant.Id), PutBody(welcomeMessage: "defaced", maxMembers: 999));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var stored = await GetSettingsFromDbAsync(tenant.Id);
        Assert.Equal("original", stored!.WelcomeMessage);
        Assert.Equal(5, stored.MaxMembers);
    }

    [Fact]
    public async Task Put_AsPlainUser_ForUnknownTenant_IsForbiddenNotNotFound()
    {
        var response = await ClientAs(Guid.NewGuid(), new[] { "User" })
            .PutAsJsonAsync(Url(Guid.NewGuid()), PutBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_AsPlainUser_WithPrivilegedDefaultRole_IsForbiddenNotBadRequest_AndCreatesNothing()
    {
        // The answer must not depend on (or reveal anything about) the payload, and nothing may be written.
        var tenant = await CreateTenantAsync();
        var admin = await CreateRoleAsync("SuperAdmin");

        var response = await ClientAs(Guid.NewGuid(), new[] { "User" })
            .PutAsJsonAsync(Url(tenant.Id), PutBody(defaultRoleId: admin.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await GetSettingsFromDbAsync(tenant.Id));
    }

    [Fact]
    public async Task Put_AsAdminOfAnotherTenant_IsForbidden_AndChangesNothing()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        await SeedSettingsAsync(other.Id, welcomeMessage: "original");
        var admin = await CreateTenantAdminAsync(own.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync(Url(other.Id), PutBody(welcomeMessage: "defaced"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("original", (await GetSettingsFromDbAsync(other.Id))!.WelcomeMessage);
    }

    [Fact]
    public async Task Put_AsTenantAdmin_WithTokenScopedToAnotherTenant_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }, tenantClaim: other.Id.ToString())
            .PutAsJsonAsync(Url(tenant.Id), PutBody(welcomeMessage: "x"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await GetSettingsFromDbAsync(tenant.Id));
    }

    [Fact]
    public async Task Put_WithRoleClaimOnly_AndNoUserRoleRow_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var user = await CreateUserAsync();

        var response = await ClientAs(user.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync(Url(tenant.Id), PutBody(welcomeMessage: "x"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_AsTenantAdmin_WithExpiredRole_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id, expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync(Url(tenant.Id), PutBody(welcomeMessage: "x"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_AsOwnTenantAdmin_UpdatesSettings()
    {
        var tenant = await CreateTenantAsync();
        await SeedSettingsAsync(tenant.Id, welcomeMessage: "original");
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync(Url(tenant.Id), PutBody(welcomeMessage: "updated by tenant admin", maxMembers: 12));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await GetSettingsFromDbAsync(tenant.Id);
        Assert.Equal("updated by tenant admin", stored!.WelcomeMessage);
        Assert.Equal(12, stored.MaxMembers);
        Assert.True(stored.RequireMfa);
    }

    [Fact]
    public async Task Put_AsOwnTenantAdmin_WithMatchingTenantClaim_CreatesSettings()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" }, tenantClaim: tenant.Id.ToString())
            .PutAsJsonAsync(Url(tenant.Id), PutBody(welcomeMessage: "fresh"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fresh", (await GetSettingsFromDbAsync(tenant.Id))!.WelcomeMessage);
    }

    [Fact]
    public async Task Put_AsSuperAdmin_UpdatesSettingsOfAnyTenant()
    {
        var tenant = await CreateTenantAsync();

        var response = await SuperAdminClient().PutAsJsonAsync(Url(tenant.Id), PutBody(welcomeMessage: "by super admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("by super admin", (await GetSettingsFromDbAsync(tenant.Id))!.WelcomeMessage);
    }

    [Fact]
    public async Task Put_AsSystemAdmin_UpdatesSettingsOfAnyTenant()
    {
        var tenant = await CreateTenantAsync();

        var response = await SystemAdminClient().PutAsJsonAsync(Url(tenant.Id), PutBody(welcomeMessage: "by system admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("by system admin", (await GetSettingsFromDbAsync(tenant.Id))!.WelcomeMessage);
    }

    [Fact]
    public async Task Put_AsSuperAdmin_ForUnknownTenant_IsNotFound()
    {
        var response = await SuperAdminClient().PutAsJsonAsync(Url(Guid.NewGuid()), PutBody());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- PUT: the default role --------------------------------------------------------------

    [Fact]
    public async Task Put_WithUnknownDefaultRole_IsBadRequest_AndChangesNothing()
    {
        var tenant = await CreateTenantAsync();
        await SeedSettingsAsync(tenant.Id, welcomeMessage: "original");

        var response = await SuperAdminClient()
            .PutAsJsonAsync(Url(tenant.Id), PutBody(defaultRoleId: Guid.NewGuid(), welcomeMessage: "changed"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await GetSettingsFromDbAsync(tenant.Id);
        Assert.Equal("original", stored!.WelcomeMessage);
        Assert.Null(stored.DefaultRoleId);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("SecurityAdmin")]
    [InlineData("BuildingOwner")]
    [InlineData("OrganizationOwner")]
    [InlineData("superadmin")]
    [InlineData("Billing Administrator")]
    public async Task Put_WithPrivilegedDefaultRole_IsBadRequest_ForTenantAdminAndSuperAdmin(string roleName)
    {
        var tenant = await CreateTenantAsync();
        await SeedSettingsAsync(tenant.Id, welcomeMessage: "original");
        var role = await CreateRoleAsync(roleName);
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var asTenantAdmin = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync(Url(tenant.Id), PutBody(defaultRoleId: role.Id, welcomeMessage: "changed"));
        var asSuperAdmin = await SuperAdminClient()
            .PutAsJsonAsync(Url(tenant.Id), PutBody(defaultRoleId: role.Id, welcomeMessage: "changed"));

        Assert.Equal(HttpStatusCode.BadRequest, asTenantAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, asSuperAdmin.StatusCode);
        var stored = await GetSettingsFromDbAsync(tenant.Id);
        Assert.Equal("original", stored!.WelcomeMessage);
        Assert.Null(stored.DefaultRoleId);
    }

    [Fact]
    public async Task Put_WithWildcardPermissionRole_AsDefaultRole_IsBadRequest()
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync("Operations", permissions: "[\"Room.*\"]");

        var response = await SuperAdminClient().PutAsJsonAsync(Url(tenant.Id), PutBody(defaultRoleId: role.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await GetSettingsFromDbAsync(tenant.Id));
    }

    [Fact]
    public async Task Put_WithDefaultRoleOwnedByAnotherTenant_IsBadRequest_EvenForSuperAdmin()
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var foreignRole = await CreateRoleAsync("Member", tenantId: other.Id);
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var asTenantAdmin = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync(Url(tenant.Id), PutBody(defaultRoleId: foreignRole.Id));
        var asSuperAdmin = await SuperAdminClient()
            .PutAsJsonAsync(Url(tenant.Id), PutBody(defaultRoleId: foreignRole.Id));

        Assert.Equal(HttpStatusCode.BadRequest, asTenantAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, asSuperAdmin.StatusCode);
        Assert.Null(await GetSettingsFromDbAsync(tenant.Id));
    }

    [Fact]
    public async Task Put_WithGlobalNonPrivilegedDefaultRole_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync("Member");
        var admin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(admin.Id, new[] { "TenantAdmin" })
            .PutAsJsonAsync(Url(tenant.Id), PutBody(defaultRoleId: role.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(role.Id, body.GetProperty("defaultRoleId").GetGuid());
        Assert.Equal("Member", body.GetProperty("defaultRoleName").GetString());
        Assert.Equal(role.Id, (await GetSettingsFromDbAsync(tenant.Id))!.DefaultRoleId);
    }

    [Fact]
    public async Task Put_WithNonPrivilegedDefaultRoleOwnedByThisTenant_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync("Tenant Member", tenantId: tenant.Id);

        var response = await SuperAdminClient().PutAsJsonAsync(Url(tenant.Id), PutBody(defaultRoleId: role.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(role.Id, (await GetSettingsFromDbAsync(tenant.Id))!.DefaultRoleId);
    }

    [Fact]
    public async Task Put_WithoutDefaultRole_KeepsTheStoredOne()
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync("Member");
        await SeedSettingsAsync(tenant.Id, defaultRoleId: role.Id);

        var response = await SuperAdminClient().PutAsJsonAsync(Url(tenant.Id), PutBody(welcomeMessage: "only the message"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(role.Id, (await GetSettingsFromDbAsync(tenant.Id))!.DefaultRoleId);
    }
}
