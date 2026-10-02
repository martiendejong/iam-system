using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4739: GET /api/identity-providers and GET /api/identity-providers/{id} are admin-only.
/// SuperAdmin/SystemAdmin read everything; a TenantAdmin (active UserRole scoped to the tenant) reads their
/// tenants' providers plus platform-wide ones and nothing of another tenant (403); everyone else gets 403 whatever
/// the id or tenantId. The anonymous /public listing and the 401 for anonymous admin reads are unchanged.
/// </summary>
public class IdentityProviderReadAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid TenantAdminRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01");

    private readonly IAMTestWebApplicationFactory _factory;

    public IdentityProviderReadAuthorizationTests(IAMTestWebApplicationFactory factory)
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

    private async Task<User> CreateTenantAdminAsync(Guid? tenantId, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@idp-read.test", PasswordHash = "not-used" };
        db.Users.Add(user);
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

    private async Task<IdentityProvider> CreateProviderAsync(Guid? tenantId)
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
            MetadataUrl = "https://idp.example.test/metadata",
            AttributeMapping = "{\"email\":\"mail\"}",
            IsActive = true
        };
        db.IdentityProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@idp-read.test", roles, tenantClaim);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient PlainUser() => ClientAs(Guid.NewGuid(), new[] { "User" });
    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });
    private HttpClient SystemAdmin() => ClientAs(Guid.NewGuid(), new[] { "SystemAdmin" });

    private async Task<HttpClient> TenantAdminClientAsync(Guid tenantId, string? tenantClaim = null)
    {
        var admin = await CreateTenantAdminAsync(tenantId);
        return ClientAs(admin.Id, new[] { "TenantAdmin" }, tenantClaim);
    }

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
    }

    // ----- plain users and anonymous ----------------------------------------------------------

    [Fact]
    public async Task List_AsPlainUser_IsForbidden_WhateverTheTenantId()
    {
        var tenant = await CreateTenantAsync();
        await CreateProviderAsync(tenant.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await PlainUser().GetAsync("/api/identity-providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PlainUser().GetAsync($"/api/identity-providers?tenantId={tenant.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PlainUser().GetAsync($"/api/identity-providers?tenantId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task GetById_AsPlainUser_IsForbidden_ForExistingAndUnknownIds()
    {
        var tenant = await CreateTenantAsync();
        var provider = await CreateProviderAsync(tenant.Id);
        var platform = await CreateProviderAsync(null);

        Assert.Equal(HttpStatusCode.Forbidden, (await PlainUser().GetAsync($"/api/identity-providers/{provider.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PlainUser().GetAsync($"/api/identity-providers/{platform.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PlainUser().GetAsync($"/api/identity-providers/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Read_AsPlainUser_DoesNotLeakConfigurationInTheBody()
    {
        var tenant = await CreateTenantAsync();
        var provider = await CreateProviderAsync(tenant.Id);

        var body = await (await PlainUser().GetAsync($"/api/identity-providers/{provider.Id}")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("seeded-client-id", body);
        Assert.DoesNotContain("idp.example.test", body);
    }

    [Fact]
    public async Task Read_Anonymous_IsUnauthorized()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/identity-providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/identity-providers/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Public_StillWorksAnonymously()
    {
        var provider = await CreateProviderAsync(null);

        var response = await _factory.CreateClient().GetAsync("/api/identity-providers/public");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(provider.Id, await IdsAsync(response));
    }

    // ----- tenant admins ----------------------------------------------------------------------

    [Fact]
    public async Task List_AsTenantAdmin_ShowsOwnTenantAndPlatformWideOnly()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var ownProvider = await CreateProviderAsync(own.Id);
        var otherProvider = await CreateProviderAsync(other.Id);
        var platform = await CreateProviderAsync(null);

        var response = await (await TenantAdminClientAsync(own.Id)).GetAsync("/api/identity-providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = await IdsAsync(response);
        Assert.Contains(ownProvider.Id, ids);
        Assert.Contains(platform.Id, ids);
        Assert.DoesNotContain(otherProvider.Id, ids);
    }

    [Fact]
    public async Task List_AsTenantAdminOfTwoTenants_ShowsBoth()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var c = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(a.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = admin.Id, RoleId = TenantAdminRoleId, TenantId = b.Id, GrantedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var pa = await CreateProviderAsync(a.Id);
        var pb = await CreateProviderAsync(b.Id);
        var pc = await CreateProviderAsync(c.Id);

        var ids = await IdsAsync(await ClientAs(admin.Id, new[] { "TenantAdmin" }).GetAsync("/api/identity-providers"));

        Assert.Contains(pa.Id, ids);
        Assert.Contains(pb.Id, ids);
        Assert.DoesNotContain(pc.Id, ids);
    }

    [Fact]
    public async Task List_AsTenantAdmin_WithOwnTenantId_ShowsOwnAndPlatformWide()
    {
        var own = await CreateTenantAsync();
        var ownProvider = await CreateProviderAsync(own.Id);
        var platform = await CreateProviderAsync(null);

        var response = await (await TenantAdminClientAsync(own.Id)).GetAsync($"/api/identity-providers?tenantId={own.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = await IdsAsync(response);
        Assert.Contains(ownProvider.Id, ids);
        Assert.Contains(platform.Id, ids);
    }

    [Fact]
    public async Task List_AsTenantAdmin_AskingForAnotherTenant_IsForbidden()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        await CreateProviderAsync(other.Id);

        var response = await (await TenantAdminClientAsync(own.Id)).GetAsync($"/api/identity-providers?tenantId={other.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Read_AsTenantAdmin_WithTokenScopedToAnotherTenant_IsForbidden()
    {
        // The user administers the tenant, but the token was issued for another one: no authority from it.
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var ownProvider = await CreateProviderAsync(own.Id);
        var admin = await CreateTenantAdminAsync(own.Id);
        var client = ClientAs(admin.Id, new[] { "TenantAdmin" }, tenantClaim: other.Id.ToString());

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/identity-providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/identity-providers/{ownProvider.Id}")).StatusCode);
    }

    [Fact]
    public async Task Read_WithRoleClaimOnly_AndNoUserRoleRow_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var provider = await CreateProviderAsync(tenant.Id);
        var client = ClientAs(Guid.NewGuid(), new[] { "TenantAdmin" });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/identity-providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/identity-providers/{provider.Id}")).StatusCode);
    }

    [Fact]
    public async Task Read_AsTenantAdmin_WithUnscopedOrExpiredRole_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var provider = await CreateProviderAsync(tenant.Id);
        var unscoped = await CreateTenantAdminAsync(null);
        var expired = await CreateTenantAdminAsync(tenant.Id, DateTime.UtcNow.AddMinutes(-5));

        foreach (var user in new[] { unscoped, expired })
        {
            var client = ClientAs(user.Id, new[] { "TenantAdmin" });
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/identity-providers")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/identity-providers/{provider.Id}")).StatusCode);
        }
    }

    [Fact]
    public async Task GetById_AsTenantAdmin_OwnPlatformWideAndOtherTenant()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var ownProvider = await CreateProviderAsync(own.Id);
        var otherProvider = await CreateProviderAsync(other.Id);
        var platform = await CreateProviderAsync(null);
        var client = await TenantAdminClientAsync(own.Id, tenantClaim: own.Id.ToString());

        var ownResponse = await client.GetAsync($"/api/identity-providers/{ownProvider.Id}");
        var platformResponse = await client.GetAsync($"/api/identity-providers/{platform.Id}");

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, platformResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/identity-providers/{otherProvider.Id}")).StatusCode);
    }

    [Fact]
    public async Task GetById_AsTenantAdmin_UnknownId_IsNotFound()
    {
        var own = await CreateTenantAsync();

        var response = await (await TenantAdminClientAsync(own.Id)).GetAsync($"/api/identity-providers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- global admins ----------------------------------------------------------------------

    [Fact]
    public async Task List_AsSuperAdminAndSystemAdmin_ShowsEveryProvider_WithTheUnchangedShape()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var pa = await CreateProviderAsync(a.Id);
        var pb = await CreateProviderAsync(b.Id);
        var platform = await CreateProviderAsync(null);

        foreach (var client in new[] { SuperAdmin(), SystemAdmin() })
        {
            var response = await client.GetAsync("/api/identity-providers");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var ids = json.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
            Assert.Contains(pa.Id, ids);
            Assert.Contains(pb.Id, ids);
            Assert.Contains(platform.Id, ids);

            var row = json.EnumerateArray().First(p => p.GetProperty("id").GetGuid() == pa.Id);
            foreach (var key in new[] { "id", "name", "displayName", "type", "tenantId", "tenantName", "clientId", "metadataUrl",
                                        "isActive", "autoCreateUsers", "defaultRoleId", "defaultRoleName", "createdAt", "updatedAt" })
                Assert.True(row.TryGetProperty(key, out _), $"missing {key}");
            Assert.False(row.TryGetProperty("clientSecret", out _));
        }
    }

    [Fact]
    public async Task List_AsSuperAdmin_WithTenantId_FiltersToTenantPlusPlatformWide()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var pa = await CreateProviderAsync(a.Id);
        var pb = await CreateProviderAsync(b.Id);
        var platform = await CreateProviderAsync(null);

        var ids = await IdsAsync(await SuperAdmin().GetAsync($"/api/identity-providers?tenantId={a.Id}"));

        Assert.Contains(pa.Id, ids);
        Assert.Contains(platform.Id, ids);
        Assert.DoesNotContain(pb.Id, ids);
    }

    [Fact]
    public async Task GetById_AsSuperAdmin_ReturnsAnyProvider_WithAttributeMapping()
    {
        var tenant = await CreateTenantAsync();
        var provider = await CreateProviderAsync(tenant.Id);

        var response = await SuperAdmin().GetAsync($"/api/identity-providers/{provider.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(provider.Id, json.GetProperty("id").GetGuid());
        Assert.Equal("seeded-client-id", json.GetProperty("clientId").GetString());
        Assert.True(json.TryGetProperty("attributeMapping", out _));
        Assert.False(json.TryGetProperty("clientSecret", out _));
    }

    [Fact]
    public async Task GetById_AsSystemAdmin_UnknownId_IsNotFound()
    {
        var response = await SystemAdmin().GetAsync($"/api/identity-providers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
