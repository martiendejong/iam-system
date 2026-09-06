using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// JengoWork task 1736: GET /api/app-roles/{clientId}/users lists the users assigned to
/// one application (holders of a "{clientId}:*" role) so the application can pre-provision
/// local user rows before a person's first login. Read-only, X-API-Key gated (same rule as
/// the register endpoint), scoped strictly to the requested client's role holders, and
/// returns directory basics only — id/email/name/active, nothing else.
/// </summary>
public class AppRolesUsersTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public AppRolesUsersTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>A real API key through the real service, used via the real middleware.</summary>
    private async Task<string> CreateApiKeyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
        var (_, rawKey) = await service.CreateApiKeyAsync("approles-users-test-key", null, null);
        return rawKey;
    }

    /// <summary>Seeds one app role + assigned users under a unique client id per test.</summary>
    private async Task<string> SeedAppUsersAsync(
        string activeEmail, string inactiveEmail, string unassignedEmail)
    {
        var clientId = $"testapp{Guid.NewGuid():N}"[..12];
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var appRole = new Role { Name = $"{clientId}:member", Category = $"app:{clientId}", TenantId = null };
        var otherAppRole = new Role { Name = $"other{clientId}:member", Category = $"app:other{clientId}", TenantId = null };
        db.Roles.AddRange(appRole, otherAppRole);

        var active = new User { Email = activeEmail, FirstName = "Sandra", LastName = "Mpoesi", IsActive = true };
        var inactive = new User { Email = inactiveEmail, FirstName = "In", LastName = "Active", IsActive = false };
        var unassigned = new User { Email = unassignedEmail, FirstName = "No", LastName = "Role", IsActive = true };
        db.Users.AddRange(active, inactive, unassigned);

        db.UserRoles.AddRange(
            new UserRole { UserId = active.Id, RoleId = appRole.Id },
            new UserRole { UserId = inactive.Id, RoleId = appRole.Id },
            // The unassigned user DOES hold a role — but for a different application.
            new UserRole { UserId = unassigned.Id, RoleId = otherAppRole.Id });
        await db.SaveChangesAsync();
        return clientId;
    }

    [Fact]
    public async Task Users_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/app-roles/taskmanager/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Users_WithInvalidApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "iam_bogus_not-a-real-key");
        var response = await client.GetAsync("/api/app-roles/taskmanager/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Users_ReturnsRoleHolders_WithActiveFlag_AndNothingSensitive()
    {
        var clientId = await SeedAppUsersAsync(
            "sandra.1736@test.com", "inactive.1736@test.com", "norole.1736@test.com");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", await CreateApiKeyAsync());
        var response = await client.GetAsync($"/api/app-roles/{clientId}/users");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(clientId, body.GetProperty("clientId").GetString());

        var users = body.GetProperty("users").EnumerateArray().ToList();
        Assert.Equal(2, users.Count);

        var sandra = users.Single(u => u.GetProperty("email").GetString() == "sandra.1736@test.com");
        Assert.Equal("Sandra", sandra.GetProperty("firstName").GetString());
        Assert.Equal("Mpoesi", sandra.GetProperty("lastName").GetString());
        Assert.True(sandra.GetProperty("isActive").GetBoolean());
        // Directory basics only — the row must never leak credential/security state.
        Assert.False(sandra.TryGetProperty("passwordHash", out _));
        Assert.False(sandra.TryGetProperty("twoFactorSecret", out _));
        Assert.False(sandra.TryGetProperty("roles", out _));

        // Deactivated-but-assigned users are included, flagged inactive, so a consumer can
        // mirror the deactivation instead of silently losing the user.
        var inactive = users.Single(u => u.GetProperty("email").GetString() == "inactive.1736@test.com");
        Assert.False(inactive.GetProperty("isActive").GetBoolean());

        // A user holding only ANOTHER application's role is not in this app's list.
        Assert.DoesNotContain(users, u => u.GetProperty("email").GetString() == "norole.1736@test.com");
    }

    [Fact]
    public async Task Users_UnknownClient_ReturnsEmptyList_NotAnError()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", await CreateApiKeyAsync());
        var response = await client.GetAsync($"/api/app-roles/never-registered-{Guid.NewGuid():N}/users");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("users").EnumerateArray());
    }
}
