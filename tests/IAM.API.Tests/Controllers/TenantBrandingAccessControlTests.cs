using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4522: TenantBrandingController's GET/PUT/DELETE endpoints must be scoped to the
/// caller's own tenant (or SuperAdmin) - any authenticated user could otherwise read, overwrite
/// or delete another tenant's branding (cross-tenant IDOR). Mirrors GroupRoleEnforcementTests's
/// pattern: default password-login tokens carry NO tenant_id claim, so authority must come from
/// an active UserRoles row, not a claim-only check.
/// </summary>
public class TenantBrandingAccessControlTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantAdminRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01");

    private readonly IAMTestWebApplicationFactory _factory;

    public TenantBrandingAccessControlTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<User> CreateUserAsync(Guid? memberOfTenant)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@tenantbranding.test",
            PasswordHash = "not-used"
        };
        db.Users.Add(user);

        if (memberOfTenant.HasValue)
        {
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RoleId = TenantAdminRoleId,
                TenantId = memberOfTenant.Value,
                GrantedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Tenant> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    /// <summary>
    /// Client authenticated as <paramref name="user"/>. Default token shape = password login:
    /// NO tenant_id claim. Pass <paramref name="tenantClaim"/> for a tenant-scoped token.
    /// </summary>
    private HttpClient ClientAs(User user, string[]? roles = null, string? tenantClaim = null)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(user.Id, user.Email, roles ?? new[] { "TenantAdmin" }, tenantClaim);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object UpsertBody() => new
    {
        logoUrl = "https://example.com/logo.png",
        primaryColor = "#123456",
        customCss = "body { color: red; }"
    };

    // ----- same-tenant allowed (the case the review found broken) ----------------------------

    [Fact]
    public async Task Get_OwnTenant_PasswordLoginToken_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateUserAsync(memberOfTenant: tenant.Id);

        var response = await ClientAs(admin).GetAsync($"/api/branding/{tenant.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_OwnTenant_PasswordLoginToken_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateUserAsync(memberOfTenant: tenant.Id);

        var response = await ClientAs(admin).PutAsJsonAsync($"/api/branding/{tenant.Id}", UpsertBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_OwnTenant_PasswordLoginToken_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateUserAsync(memberOfTenant: tenant.Id);
        await ClientAs(admin).PutAsJsonAsync($"/api/branding/{tenant.Id}", UpsertBody());

        var response = await ClientAs(admin).DeleteAsync($"/api/branding/{tenant.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_OwnTenant_WithMatchingTenantClaim_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateUserAsync(memberOfTenant: tenant.Id);

        var response = await ClientAs(admin, tenantClaim: tenant.Id.ToString()).GetAsync($"/api/branding/{tenant.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ----- cross-tenant blocked (the reported IDOR) -------------------------------------------

    [Fact]
    public async Task Get_OtherTenant_IsForbidden()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var adminOfA = await CreateUserAsync(memberOfTenant: tenantA.Id);

        var response = await ClientAs(adminOfA).GetAsync($"/api/branding/{tenantB.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_OtherTenant_IsForbidden()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var adminOfA = await CreateUserAsync(memberOfTenant: tenantA.Id);

        var response = await ClientAs(adminOfA).PutAsJsonAsync($"/api/branding/{tenantB.Id}", UpsertBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delete_OtherTenant_IsForbidden()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var adminOfA = await CreateUserAsync(memberOfTenant: tenantA.Id);

        var response = await ClientAs(adminOfA).DeleteAsync($"/api/branding/{tenantB.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithoutAnyTenantMembership_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var outsider = await CreateUserAsync(memberOfTenant: null);

        var response = await ClientAs(outsider).GetAsync($"/api/branding/{tenant.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_TenantScopedTokenForDifferentTenant_IsForbidden()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        // Member of A, but token carries a tenant_id claim scoping it to B.
        var adminOfA = await CreateUserAsync(memberOfTenant: tenantA.Id);

        var response = await ClientAs(adminOfA, tenantClaim: tenantB.Id.ToString()).GetAsync($"/api/branding/{tenantB.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- SuperAdmin bypass ---------------------------------------------------------------

    [Fact]
    public async Task Get_AsSuperAdmin_CanAccessAnyTenant()
    {
        var tenant = await CreateTenantAsync();
        var superAdmin = await CreateUserAsync(memberOfTenant: null);

        var response = await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).GetAsync($"/api/branding/{tenant.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_AsSuperAdmin_CanManageAnyTenant()
    {
        var tenant = await CreateTenantAsync();
        var superAdmin = await CreateUserAsync(memberOfTenant: null);

        var response = await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).PutAsJsonAsync($"/api/branding/{tenant.Id}", UpsertBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_AsSuperAdmin_CanManageAnyTenant()
    {
        var tenant = await CreateTenantAsync();
        var superAdmin = await CreateUserAsync(memberOfTenant: null);
        await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).PutAsJsonAsync($"/api/branding/{tenant.Id}", UpsertBody());

        var response = await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).DeleteAsync($"/api/branding/{tenant.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ----- anonymous endpoints must stay untouched ---------------------------------------------

    [Fact]
    public async Task PublicBySlug_RequiresNoAuthentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/branding/public/nonexistent-slug");

        // No [Authorize] challenge (401) - a genuine 404 for the missing tenant proves the
        // anonymous route still short-circuits before any tenant-ownership check.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ByDomain_RequiresNoAuthentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/branding/by-domain/nonexistent.example.com");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
