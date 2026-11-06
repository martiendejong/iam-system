using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5487: POST /api/app-roles/register is bound to the calling application. A key may register a catalog only
/// for the client id it is bound to (permission "app-roles:register.{clientId}"), the client must be a registered
/// OIDC client, and the first catalog for an app needs an admin-scope key or the "app-roles:create.{clientId}" binding.
/// </summary>
public class AppRolesRegisterBindingTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public AppRolesRegisterBindingTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private static string Slug() => $"app{Guid.NewGuid():N}"[..12];

    private async Task<HttpClient> ClientWithKeyAsync(string scope, Guid? tenantId = null, params string[] permissions)
    {
        using var scoped = _factory.Services.CreateScope();
        var service = scoped.ServiceProvider.GetRequiredService<IApiKeyService>();
        var (_, raw) = await service.CreateApiKeyAsync(
            $"t5487-{Guid.NewGuid():N}"[..20], null, tenantId, permissions.ToList(), scope: scope);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", raw);
        return client;
    }

    private async Task<string> SeedOidcClientAsync(bool withCatalog)
    {
        var clientId = Slug();
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await manager.CreateAsync(new OpenIddictApplicationDescriptor { ClientId = clientId, DisplayName = clientId });
        if (withCatalog)
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            db.Roles.Add(new Role { Name = $"{clientId}:member", Category = $"app:{clientId}", TenantId = null, Description = "old" });
            await db.SaveChangesAsync();
        }
        return clientId;
    }

    private async Task<List<Role>> RolesForAsync(string clientId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Roles.AsNoTracking().Where(r => r.Category == $"app:{clientId}").ToListAsync();
    }

    private static object Body(string clientId, string name = "member", string description = "new") =>
        new { clientId, roles = new[] { new { name, description } } };

    [Fact]
    public async Task UnknownClient_IsRefused_AndNothingIsWritten()
    {
        var clientId = Slug();
        var client = await ClientWithKeyAsync("admin");

        var response = await client.PostAsJsonAsync("/api/app-roles/register", Body(clientId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await RolesForAsync(clientId));
    }

    [Fact]
    public async Task UnknownClient_WithBoundKey_IsRefused()
    {
        var clientId = Slug();
        var client = await ClientWithKeyAsync("write", null, $"app-roles:create.{clientId}");

        var response = await client.PostAsJsonAsync("/api/app-roles/register", Body(clientId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await RolesForAsync(clientId));
    }

    [Fact]
    public async Task UnboundKey_IsForbidden_AndCannotOverwriteAnotherAppsCatalog()
    {
        var victim = await SeedOidcClientAsync(withCatalog: true);
        var own = await SeedOidcClientAsync(withCatalog: true);
        var client = await ClientWithKeyAsync("write", null, $"app-roles:register.{own}");

        var response = await client.PostAsJsonAsync("/api/app-roles/register", Body(victim));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("old", Assert.Single(await RolesForAsync(victim)).Description);
    }

    [Fact]
    public async Task KeyWithNoBinding_IsForbidden()
    {
        var clientId = await SeedOidcClientAsync(withCatalog: true);
        var client = await ClientWithKeyAsync("write");

        var response = await client.PostAsJsonAsync("/api/app-roles/register", Body(clientId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BoundKey_UpdatesItsOwnExistingCatalog()
    {
        var clientId = await SeedOidcClientAsync(withCatalog: true);
        var client = await ClientWithKeyAsync("write", null, $"app-roles:register.{clientId}");

        var response = await client.PostAsJsonAsync("/api/app-roles/register", Body(clientId, "member", "updated"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("updated", Assert.Single(await RolesForAsync(clientId)).Description);
    }

    [Fact]
    public async Task FirstCatalog_WithOnlyRegisterBinding_IsForbidden_AndNothingIsWritten()
    {
        var clientId = await SeedOidcClientAsync(withCatalog: false);
        var client = await ClientWithKeyAsync("write", null, $"app-roles:register.{clientId}");

        var response = await client.PostAsJsonAsync("/api/app-roles/register", Body(clientId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await RolesForAsync(clientId));
    }

    [Fact]
    public async Task FirstCatalog_WithAdminKey_Succeeds_WithPrefixedNamesAndCategory()
    {
        var clientId = await SeedOidcClientAsync(withCatalog: false);
        var client = await ClientWithKeyAsync("admin");

        var response = await client.PostAsJsonAsync("/api/app-roles/register", Body(clientId, "Admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var role = Assert.Single(await RolesForAsync(clientId));
        Assert.Equal($"{clientId}:admin", role.Name);
        Assert.Equal($"app:{clientId}", role.Category);
        Assert.Null(role.TenantId);
    }

    [Fact]
    public async Task FirstCatalog_WithCreateBinding_Succeeds()
    {
        var clientId = await SeedOidcClientAsync(withCatalog: false);
        var client = await ClientWithKeyAsync("write", null, $"app-roles:create.{clientId}");

        var response = await client.PostAsJsonAsync("/api/app-roles/register", Body(clientId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await RolesForAsync(clientId));
    }

    [Fact]
    public async Task TenantScopedKey_IsStillForbidden_EvenWhenBound()
    {
        var clientId = await SeedOidcClientAsync(withCatalog: true);
        var client = await ClientWithKeyAsync("write", Guid.NewGuid(), $"app-roles:register.{clientId}");

        var response = await client.PostAsJsonAsync("/api/app-roles/register", Body(clientId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("old", Assert.Single(await RolesForAsync(clientId)).Description);
    }
}
