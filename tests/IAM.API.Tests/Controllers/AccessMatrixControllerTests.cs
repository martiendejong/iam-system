using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Integration tests for the admin access matrix (task 1737):
/// grant/revoke reflected in role assignments (the source of token role claims),
/// audit rows with before/after, self-lockout prevention, 403 for non-admins,
/// and manifest-driven role validation.
/// </summary>
public class AccessMatrixControllerTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid AdminUserId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid TargetUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private readonly IAMTestWebApplicationFactory _factory;
    private readonly HttpClient _superAdminClient;

    public AccessMatrixControllerTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _superAdminClient = factory.CreateClient();
        _superAdminClient.AddAuthorizationHeader(
            TestAuthenticationHelper.GenerateJwtToken(AdminUserId, "admin@test.com", new[] { "SuperAdmin" }));
    }

    [Fact]
    public async Task GetApplications_ReturnsManifestApps_WithPermissions()
    {
        var response = await _superAdminClient.GetAsync("/api/access-matrix/applications");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apps = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(apps);
        Assert.NotEmpty(apps);

        // Manifest entry from appsettings.json: JengoWork with its permission set.
        // Role names here must match TaskManager's own federated app-role catalog
        // ("taskmanager:{role}", registered via POST /api/app-roles/register on every
        // TaskManager boot — see AppRolesController) — NOT an invented "app:jengowork*"
        // convention, since AuthorizationController.Authorize()'s per-app access gate
        // matches sign-in against that exact "{clientId}:" prefix once a catalog exists
        // for the client. See AccessMatrixTaskmanagerRoleNames_MatchFederatedCatalogPrefix
        // below for the regression guard.
        var jengowork = apps!.FirstOrDefault(a => a.GetProperty("clientId").GetString() == "taskmanager");
        Assert.NotEqual(JsonValueKind.Undefined, jengowork.ValueKind);
        Assert.Equal("taskmanager:developer", jengowork.GetProperty("baseRole").GetString());
        var permissions = jengowork.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetProperty("role").GetString())
            .ToList();
        Assert.Contains("taskmanager:admin", permissions);
        Assert.Contains("taskmanager:product-owner", permissions);
    }

    [Fact]
    public async Task AccessMatrixTaskmanagerRoleNames_MatchFederatedCatalogPrefix()
    {
        // Regression guard for task 1737 follow-up: the "taskmanager" (JengoWork) entry's
        // baseRole and every permission role must start with "taskmanager:" so that granting
        // them via the matrix actually satisfies AuthorizationController.Authorize()'s
        // federated app-role gate (hasAppRole = user has a role starting with "{clientId}:").
        // A manifest entry using any other naming convention (e.g. "app:jengowork*") looks
        // fully functional in the matrix UI but grants a role the sign-in gate never checks —
        // toggling it does nothing for the user's actual ability to sign in to the app.
        var response = await _superAdminClient.GetAsync("/api/access-matrix/applications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apps = await response.Content.ReadFromJsonAsync<List<JsonElement>>();

        var taskmanager = apps!.Single(a => a.GetProperty("clientId").GetString() == "taskmanager");
        Assert.StartsWith("taskmanager:", taskmanager.GetProperty("baseRole").GetString());
        foreach (var perm in taskmanager.GetProperty("permissions").EnumerateArray())
        {
            Assert.StartsWith("taskmanager:", perm.GetProperty("role").GetString());
        }
    }

    [Fact]
    public async Task GetMatrix_ReturnsUsersAndApplications()
    {
        var response = await _superAdminClient.GetAsync("/api/access-matrix");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("total").GetInt32() >= 2);
        Assert.NotEmpty(body.GetProperty("applications").EnumerateArray());
        var users = body.GetProperty("users").EnumerateArray().ToList();
        Assert.Contains(users, u => u.GetProperty("email").GetString() == "user@test.com");
    }

    [Fact]
    public async Task GetMatrix_Search_FiltersUsers()
    {
        var response = await _superAdminClient.GetAsync("/api/access-matrix?search=user@test.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var users = body.GetProperty("users").EnumerateArray().ToList();
        Assert.Single(users);
        Assert.Equal("user@test.com", users[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task Grant_CreatesRoleAssignment_AndAuditRow()
    {
        await _factory.EnsureSeededAsync();

        var response = await _superAdminClient.PostAsJsonAsync("/api/access-matrix/grant",
            new { userId = TargetUserId, role = "app:vault" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("changed").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        // Role assignment exists — this is exactly what token issuance copies
        // into role claims (AuthorizationController reads UserRoles.Role.Name),
        // so the claim appears on the user's next token refresh.
        var roleNames = await db.UserRoles
            .Where(ur => ur.UserId == TargetUserId)
            .Select(ur => ur.Role.Name)
            .ToListAsync();
        Assert.Contains("app:vault", roleNames);

        // Audit row with before/after
        var audit = await db.AuditLogs
            .Where(a => a.Action == "AccessMatrix.RoleGranted")
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
        Assert.Equal(AdminUserId, audit!.UserId);
        Assert.Equal("UserRole", audit.Resource);
        var details = JsonSerializer.Deserialize<JsonElement>(audit.Details!);
        Assert.Equal(TargetUserId.ToString(), details.GetProperty("targetUserId").GetString());
        Assert.Equal("app:vault", details.GetProperty("role").GetString());
        Assert.DoesNotContain("app:vault",
            details.GetProperty("before").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("app:vault",
            details.GetProperty("after").EnumerateArray().Select(e => e.GetString()));

        // Matrix reflects it live
        var matrix = await _superAdminClient.GetAsync("/api/access-matrix?search=user@test.com");
        var matrixBody = await matrix.Content.ReadFromJsonAsync<JsonElement>();
        var user = matrixBody.GetProperty("users").EnumerateArray().Single();
        Assert.Contains("app:vault",
            user.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Grant_Twice_IsIdempotent()
    {
        await _factory.EnsureSeededAsync();

        var first = await _superAdminClient.PostAsJsonAsync("/api/access-matrix/grant",
            new { userId = TargetUserId, role = "app:workspace" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _superAdminClient.PostAsJsonAsync("/api/access-matrix/grant",
            new { userId = TargetUserId, role = "app:workspace" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("changed").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var count = await db.UserRoles
            .CountAsync(ur => ur.UserId == TargetUserId && ur.Role.Name == "app:workspace");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Revoke_RemovesAssignment_AndAuditRow()
    {
        await _factory.EnsureSeededAsync();

        // Arrange: grant a fine-grained permission first
        var grant = await _superAdminClient.PostAsJsonAsync("/api/access-matrix/grant",
            new { userId = TargetUserId, role = "app:vault:approver" });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        // Act
        var revoke = await _superAdminClient.PostAsJsonAsync("/api/access-matrix/revoke",
            new { userId = TargetUserId, role = "app:vault:approver" });

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var body = await revoke.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("changed").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var stillThere = await db.UserRoles
            .AnyAsync(ur => ur.UserId == TargetUserId && ur.Role.Name == "app:vault:approver");
        Assert.False(stillThere);

        var audit = await db.AuditLogs
            .Where(a => a.Action == "AccessMatrix.RoleRevoked")
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
        var details = JsonSerializer.Deserialize<JsonElement>(audit!.Details!);
        Assert.Contains("app:vault:approver",
            details.GetProperty("before").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("app:vault:approver",
            details.GetProperty("after").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Revoke_OwnSuperAdmin_IsRejected()
    {
        var response = await _superAdminClient.PostAsJsonAsync("/api/access-matrix/revoke",
            new { userId = AdminUserId, role = "SuperAdmin" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("your own SuperAdmin", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GrantOrRevoke_SuperAdmin_NeverManageable()
    {
        // SuperAdmin is hard-excluded from the managed role set, even for other users
        var grant = await _superAdminClient.PostAsJsonAsync("/api/access-matrix/grant",
            new { userId = TargetUserId, role = "SuperAdmin" });
        Assert.Equal(HttpStatusCode.BadRequest, grant.StatusCode);

        var revoke = await _superAdminClient.PostAsJsonAsync("/api/access-matrix/revoke",
            new { userId = TargetUserId, role = "SuperAdmin" });
        Assert.Equal(HttpStatusCode.BadRequest, revoke.StatusCode);
    }

    [Fact]
    public async Task Grant_UnknownRole_IsRejected()
    {
        var response = await _superAdminClient.PostAsJsonAsync("/api/access-matrix/grant",
            new { userId = TargetUserId, role = "app:not-in-any-manifest" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("not managed by the access matrix", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NonAdmin_GetsForbidden_OnAllEndpoints()
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        var get = await client.GetAsync("/api/access-matrix");
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);

        var apps = await client.GetAsync("/api/access-matrix/applications");
        Assert.Equal(HttpStatusCode.Forbidden, apps.StatusCode);

        var grant = await client.PostAsJsonAsync("/api/access-matrix/grant",
            new { userId = TargetUserId, role = "app:vault" });
        Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);

        var revoke = await client.PostAsJsonAsync("/api/access-matrix/revoke",
            new { userId = TargetUserId, role = "app:vault" });
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    [Fact]
    public async Task Anonymous_GetsUnauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/access-matrix");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
