using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Integration tests for task 4057: manager link + principal kind on users and
/// service accounts (PUT /api/users/{id}/principal and
/// PUT /api/service-accounts/{id}/principal).
/// Covers defaults, set/clear, self/2-/3-cycles, depth 8 ok / 9 rejected
/// (including reports below the moved principal), cross-tenant rules, 403s,
/// disabled managers and audit rows.
/// </summary>
public class PrincipalManagerTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SeededUserRoleId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SuperAdminActorId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly IAMTestWebApplicationFactory _factory;
    private readonly HttpClient _superAdmin;
    private readonly HttpClient _systemAdmin;

    public PrincipalManagerTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;

        _superAdmin = factory.CreateClient();
        _superAdmin.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            SuperAdminActorId, "admin@test.com", new[] { "SuperAdmin" }));

        _systemAdmin = factory.CreateClient();
        _systemAdmin.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            SuperAdminActorId, "admin@test.com", new[] { "SystemAdmin" }));
    }

    // ---- helpers -----------------------------------------------------------

    private Guid CreateUser(bool isActive = true, Guid? managerUserId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User
        {
            Email = $"principal-{Guid.NewGuid():N}@test.com",
            PasswordHash = "not-a-real-hash",
            FirstName = "Principal",
            LastName = "Test",
            IsActive = isActive,
            ManagerUserId = managerUserId
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>Creates a straight chain u1 &lt;- u2 &lt;- ... and returns the ids top-down.</summary>
    private List<Guid> CreateUserChain(int length)
    {
        var ids = new List<Guid>();
        Guid? previous = null;
        for (var i = 0; i < length; i++)
        {
            var id = CreateUser(managerUserId: previous);
            ids.Add(id);
            previous = id;
        }
        return ids;
    }

    private Guid CreateServiceAccount(Guid? tenantId = null, Guid? managerUserId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var account = new ServiceAccount
        {
            Name = $"svc-{Guid.NewGuid():N}",
            ClientId = $"svc_{Guid.NewGuid():N}",
            ClientSecretHash = "not-a-real-hash",
            TenantId = tenantId,
            ManagerUserId = managerUserId
        };
        db.ServiceAccounts.Add(account);
        db.SaveChanges();
        return account.Id;
    }

    private Guid CreateTenant()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Name = $"T-{Guid.NewGuid():N}", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant.Id;
    }

    private void GiveUserRoleInTenant(Guid userId, Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            RoleId = SeededUserRoleId,
            TenantId = tenantId
        });
        db.SaveChanges();
    }

    private static object PrincipalBody(Guid? managerUserId, string kind) =>
        new { managerUserId, principalKind = kind };

    private Task<HttpResponseMessage> PutUserPrincipal(HttpClient client, Guid userId, Guid? managerUserId, string kind) =>
        client.PutAsJsonAsync($"/api/users/{userId}/principal", PrincipalBody(managerUserId, kind));

    private Task<HttpResponseMessage> PutServiceAccountPrincipal(HttpClient client, Guid accountId, Guid? managerUserId, string kind) =>
        client.PutAsJsonAsync($"/api/service-accounts/{accountId}/principal", PrincipalBody(managerUserId, kind));

    private async Task<JsonElement> GetUserJson(Guid userId)
    {
        var response = await _superAdmin.GetAsync($"/api/users/{userId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetServiceAccountJson(Guid accountId)
    {
        var response = await _superAdmin.GetAsync($"/api/service-accounts/{accountId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private List<AuditLog> GetPrincipalAuditRows(Guid targetId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return db.AuditLogs
            .Where(a => a.Action == "PrincipalChanged" && a.Details != null && a.Details.Contains(targetId.ToString()))
            .ToList();
    }

    // ---- defaults ----------------------------------------------------------

    [Fact]
    public async Task NewUser_DefaultsToHumanKind_WithoutManager()
    {
        var userId = CreateUser();

        var json = await GetUserJson(userId);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("managerUserId").ValueKind);
        Assert.Equal("Human", json.GetProperty("principalKind").GetString());
    }

    [Fact]
    public async Task NewServiceAccount_DefaultsToServiceKind_WithoutManager()
    {
        var accountId = CreateServiceAccount();

        var json = await GetServiceAccountJson(accountId);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("managerUserId").ValueKind);
        Assert.Equal("Service", json.GetProperty("principalKind").GetString());
    }

    // ---- set / clear -------------------------------------------------------

    [Fact]
    public async Task SetUserManagerAndKind_Persists_AndShowsInRead()
    {
        var managerId = CreateUser();
        var userId = CreateUser();

        var response = await PutUserPrincipal(_superAdmin, userId, managerId, "Agent");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await GetUserJson(userId);
        Assert.Equal(managerId, json.GetProperty("managerUserId").GetGuid());
        Assert.Equal("Agent", json.GetProperty("principalKind").GetString());
    }

    [Fact]
    public async Task ClearManager_WithNull_RemovesManager()
    {
        var managerId = CreateUser();
        var userId = CreateUser();

        Assert.Equal(HttpStatusCode.OK, (await PutUserPrincipal(_superAdmin, userId, managerId, "Human")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutUserPrincipal(_superAdmin, userId, null, "Human")).StatusCode);

        var json = await GetUserJson(userId);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("managerUserId").ValueKind);
    }

    [Fact]
    public async Task MissingPrincipalKind_Returns400()
    {
        var userId = CreateUser();

        var response = await _superAdmin.PutAsJsonAsync(
            $"/api/users/{userId}/principal", new { managerUserId = (Guid?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownManager_Returns400()
    {
        var userId = CreateUser();

        var response = await PutUserPrincipal(_superAdmin, userId, Guid.NewGuid(), "Human");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownTargetUser_Returns404()
    {
        var response = await PutUserPrincipal(_superAdmin, Guid.NewGuid(), null, "Human");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- self / cycles -----------------------------------------------------

    [Fact]
    public async Task SelfManager_Returns400()
    {
        var userId = CreateUser();

        var response = await PutUserPrincipal(_superAdmin, userId, userId, "Human");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TwoNodeCycle_Returns400()
    {
        var a = CreateUser();
        var b = CreateUser(managerUserId: a); // b reports to a

        var response = await PutUserPrincipal(_superAdmin, a, b, "Human"); // a -> b -> a

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ThreeNodeCycle_Returns400()
    {
        var a = CreateUser();
        var b = CreateUser(managerUserId: a);
        var c = CreateUser(managerUserId: b); // a <- b <- c

        var response = await PutUserPrincipal(_superAdmin, a, c, "Human"); // a -> c -> b -> a

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- depth -------------------------------------------------------------

    [Fact]
    public async Task ChainDepth8_IsAllowed_Depth9_IsRejected()
    {
        var chain = CreateUserChain(7); // u1 (top) .. u7
        var u8 = CreateUser();
        var u9 = CreateUser();

        // 7 ancestors + moved user = 8 -> OK
        var ok = await PutUserPrincipal(_superAdmin, u8, chain[^1], "Human");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // 8 ancestors + moved user = 9 -> rejected
        var tooDeep = await PutUserPrincipal(_superAdmin, u9, u8, "Human");
        Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);
        var body = await tooDeep.Content.ReadAsStringAsync();
        Assert.Contains("8", body);
    }

    [Fact]
    public async Task Depth_CountsReportsBelowTheMovedUser()
    {
        var chain = CreateUserChain(6); // 6 ancestors when hooking under chain[^1]

        // moved user with a 2-level subtree below: 6 + 1 + 2 = 9 -> rejected
        var movedDeep = CreateUser();
        var report = CreateUser(managerUserId: movedDeep);
        _ = CreateUser(managerUserId: report);
        var rejected = await PutUserPrincipal(_superAdmin, movedDeep, chain[^1], "Human");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // moved user with a 1-level subtree below: 6 + 1 + 1 = 8 -> OK
        var movedOk = CreateUser();
        _ = CreateUser(managerUserId: movedOk);
        var allowed = await PutUserPrincipal(_superAdmin, movedOk, chain[^1], "Human");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Depth_CountsServiceAccountReports_AsOneLeafLevel()
    {
        var chain = CreateUserChain(7); // 7 ancestors when hooking under chain[^1]

        // moved user managing a service account: 7 + 1 + 1 = 9 -> rejected
        var moved = CreateUser();
        _ = CreateServiceAccount(managerUserId: moved);

        var response = await PutUserPrincipal(_superAdmin, moved, chain[^1], "Human");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- tenants -----------------------------------------------------------

    [Fact]
    public async Task CrossTenantManager_AsSystemAdmin_Returns400()
    {
        var otherTenantId = CreateTenant();
        var managerId = CreateUser();
        GiveUserRoleInTenant(managerId, otherTenantId);
        var userId = CreateUser();
        GiveUserRoleInTenant(userId, RootTenantId);

        var response = await PutUserPrincipal(_systemAdmin, userId, managerId, "Human");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenantManager_AsSuperAdmin_IsAllowed()
    {
        var otherTenantId = CreateTenant();
        var managerId = CreateUser();
        GiveUserRoleInTenant(managerId, otherTenantId);
        var userId = CreateUser();
        GiveUserRoleInTenant(userId, RootTenantId);

        var response = await PutUserPrincipal(_superAdmin, userId, managerId, "Human");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SharedTenantManager_AsSystemAdmin_IsAllowed()
    {
        var managerId = CreateUser();
        GiveUserRoleInTenant(managerId, RootTenantId);
        var userId = CreateUser();
        GiveUserRoleInTenant(userId, RootTenantId);

        var response = await PutUserPrincipal(_systemAdmin, userId, managerId, "Human");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BothUntenanted_CountsAsSameTenant_ForSystemAdmin()
    {
        var managerId = CreateUser(); // no roles -> no tenants
        var userId = CreateUser();    // no roles -> no tenants

        var response = await PutUserPrincipal(_systemAdmin, userId, managerId, "Human");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ServiceAccount_CrossTenantManager_AsSystemAdmin_Returns400_ButSuperAdminAllowed()
    {
        var otherTenantId = CreateTenant();
        var accountId = CreateServiceAccount(tenantId: otherTenantId);
        var managerId = CreateUser();
        GiveUserRoleInTenant(managerId, RootTenantId);

        var rejected = await PutServiceAccountPrincipal(_systemAdmin, accountId, managerId, "Service");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var allowed = await PutServiceAccountPrincipal(_superAdmin, accountId, managerId, "Service");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // ---- disabled manager --------------------------------------------------

    [Fact]
    public async Task DisabledManager_CanStillBeStored()
    {
        var managerId = CreateUser(isActive: false);
        var userId = CreateUser();

        var response = await PutUserPrincipal(_superAdmin, userId, managerId, "Human");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await GetUserJson(userId);
        Assert.Equal(managerId, json.GetProperty("managerUserId").GetGuid());
    }

    // ---- authorization -----------------------------------------------------

    [Fact]
    public async Task NonAdminUser_Gets403_OnBothEndpoints()
    {
        var userId = CreateUser();
        var accountId = CreateServiceAccount();

        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutUserPrincipal(client, userId, null, "Human")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutServiceAccountPrincipal(client, accountId, null, "Service")).StatusCode);
    }

    [Fact]
    public async Task ServiceAccountToken_Gets403_OnBothEndpoints()
    {
        var userId = CreateUser();
        var accountId = CreateServiceAccount();

        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(
            TestAuthenticationHelper.GenerateServiceAccountToken("svc_principal_test", "users:create"));

        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutUserPrincipal(client, userId, null, "Human")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutServiceAccountPrincipal(client, accountId, null, "Service")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_Gets401()
    {
        var userId = CreateUser();
        var client = _factory.CreateClient();

        var response = await PutUserPrincipal(client, userId, null, "Human");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- audit -------------------------------------------------------------

    [Fact]
    public async Task UserPrincipalChange_WritesAuditRow_WithActorBeforeAfter()
    {
        var managerId = CreateUser();
        var userId = CreateUser();

        Assert.Equal(HttpStatusCode.OK,
            (await PutUserPrincipal(_superAdmin, userId, managerId, "Agent")).StatusCode);

        var rows = GetPrincipalAuditRows(userId);
        var row = Assert.Single(rows);
        Assert.Equal(SuperAdminActorId, row.UserId); // actor
        Assert.Equal("User", row.Resource);
        Assert.NotNull(row.Details);

        var details = JsonDocument.Parse(row.Details!).RootElement;
        Assert.Equal(userId, details.GetProperty("targetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, details.GetProperty("before").GetProperty("managerUserId").ValueKind);
        Assert.Equal("Human", details.GetProperty("before").GetProperty("principalKind").GetString());
        Assert.Equal(managerId, details.GetProperty("after").GetProperty("managerUserId").GetGuid());
        Assert.Equal("Agent", details.GetProperty("after").GetProperty("principalKind").GetString());
    }

    [Fact]
    public async Task ServiceAccountPrincipalChange_WritesAuditRow()
    {
        var managerId = CreateUser();
        var accountId = CreateServiceAccount();

        Assert.Equal(HttpStatusCode.OK,
            (await PutServiceAccountPrincipal(_superAdmin, accountId, managerId, "Agent")).StatusCode);

        var rows = GetPrincipalAuditRows(accountId);
        var row = Assert.Single(rows);
        Assert.Equal("ServiceAccount", row.Resource);

        var details = JsonDocument.Parse(row.Details!).RootElement;
        Assert.Equal("Service", details.GetProperty("before").GetProperty("principalKind").GetString());
        Assert.Equal("Agent", details.GetProperty("after").GetProperty("principalKind").GetString());
    }

    [Fact]
    public async Task NoOpPut_DoesNotWriteAuditRow()
    {
        var userId = CreateUser();

        // Same values as the defaults -> nothing changes -> no audit entry
        Assert.Equal(HttpStatusCode.OK,
            (await PutUserPrincipal(_superAdmin, userId, null, "Human")).StatusCode);

        Assert.Empty(GetPrincipalAuditRows(userId));
    }

    [Fact]
    public async Task EveryChange_WritesItsOwnAuditRow()
    {
        var managerId = CreateUser();
        var userId = CreateUser();

        Assert.Equal(HttpStatusCode.OK,
            (await PutUserPrincipal(_superAdmin, userId, managerId, "Human")).StatusCode); // manager change
        Assert.Equal(HttpStatusCode.OK,
            (await PutUserPrincipal(_superAdmin, userId, managerId, "Agent")).StatusCode); // kind change
        Assert.Equal(HttpStatusCode.OK,
            (await PutUserPrincipal(_superAdmin, userId, null, "Agent")).StatusCode);      // manager cleared

        Assert.Equal(3, GetPrincipalAuditRows(userId).Count);
    }
    [Fact]
    public async Task SetPrincipal_UndefinedKindInteger_IsRejectedWith400()
    {
        // Review finding on PR #137: JsonStringEnumConverter accepts undefined numerics, so
        // {"principalKind":99} used to persist as (PrincipalKind)99.
        var target = CreateUser();

        var response = await _superAdmin.PutAsJsonAsync($"/api/users/{target}/principal",
            new { principalKind = 99, managerUserId = (Guid?)null });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var stored = await GetUserJson(target);
        Assert.Equal("Human", stored.GetProperty("principalKind").GetString());
    }

}
