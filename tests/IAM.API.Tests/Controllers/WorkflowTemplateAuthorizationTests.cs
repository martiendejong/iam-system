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
/// Task 5152: workflow templates decide who approves access requests and whether they are approved automatically, so
/// only a SuperAdmin or an active BuildingOwner/BuildingManager of the template's tenant may touch them (global
/// templates: SuperAdmin only). An auto-approve rule needs a narrowing condition on save and is ignored at approval
/// time when it has none (also when stored before), and an access request needs an existing role and tenant.
/// </summary>
public class WorkflowTemplateAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IAMTestWebApplicationFactory _factory;

    public WorkflowTemplateAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------

    private async Task<Guid> CreateTenantAsync(bool active = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = active };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@wf-authz.test", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> CreateRoleAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var role = new Role { Id = Guid.NewGuid(), Name = $"Reader-{Guid.NewGuid():N}", Description = "r", TenantId = RootTenantId };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
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
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id,
            TenantId = tenantId,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTemplateAsync(Guid? tenantId, string resourceType = "role", string? autoApproveRules = null, bool isActive = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var template = new WorkflowTemplate
        {
            TenantId = tenantId,
            Name = "seeded",
            ResourceType = resourceType,
            Steps = "[]",
            AutoApproveRules = autoApproveRules,
            IsActive = isActive
        };
        db.WorkflowTemplates.Add(template);
        await db.SaveChangesAsync();
        return template.Id;
    }

    private async Task<WorkflowTemplate?> TemplateRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.WorkflowTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
    }

    private async Task<List<WorkflowTemplate>> TemplatesByNameAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.WorkflowTemplates.AsNoTracking().Where(t => t.Name == name).ToListAsync();
    }

    private async Task<AccessRequest?> RequestRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.AccessRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, string[] roles) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@wf-authz.test", roles, null));

    private async Task<(Guid UserId, HttpClient Client)> UserWithRoleAsync(string roleName, Guid? tenantId, DateTime? expiresAt = null)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, roleName, tenantId, expiresAt);
        return (userId, ClientAs(userId, new[] { roleName }));
    }

    private async Task<(Guid UserId, HttpClient Client)> OrdinaryUserAsync()
    {
        var userId = await CreateUserAsync();
        return (userId, ClientAs(userId, new[] { "User" }));
    }

    private async Task<HttpClient> SuperAdminAsync() => ClientAs(await CreateUserAsync(), new[] { "SuperAdmin" });

    private static object TemplateBody(Guid? tenantId, string? name = null, string? rules = null, string resourceType = "role") => new
    {
        name = name ?? $"tpl-{Guid.NewGuid():N}",
        description = "d",
        resourceType,
        tenantId,
        steps = "[]",
        autoExpireHours = 24,
        autoApproveRules = rules,
        isActive = true
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<List<Guid>> TemplateIdsAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await JsonAsync(r)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    private static string RoleRule(Guid roleId) => $"[{{\"RoleId\":\"{roleId}\"}}]";

    private static async Task<HttpStatusCode[]> AllTemplateCallsAsync(HttpClient c, Guid? tenantId, Guid templateId) => new[]
    {
        (await c.PostAsJsonAsync("/api/workflow-templates", TemplateBody(tenantId))).StatusCode,
        (await c.PutAsJsonAsync($"/api/workflow-templates/{templateId}", TemplateBody(tenantId, name: "renamed"))).StatusCode,
        (await c.DeleteAsync($"/api/workflow-templates/{templateId}")).StatusCode,
        (await c.GetAsync("/api/workflow-templates")).StatusCode,
        (await c.GetAsync($"/api/workflow-templates/{templateId}")).StatusCode,
    };

    private static readonly string[] CallNames = { "create", "update", "delete", "list", "get" };

    private static void AssertAll(HttpStatusCode expected, HttpStatusCode[] statuses)
    {
        for (var i = 0; i < statuses.Length; i++)
            Assert.True(statuses[i] == expected, $"{CallNames[i]} returned {statuses[i]}, expected {expected}");
    }

    private async Task<(Guid RequestId, string Status)> FileRequestAsync(HttpClient client, Guid roleId, Guid tenantId)
    {
        var response = await client.PostAsJsonAsync("/api/access-requests", new
        {
            resourceType = "role",
            roleId,
            tenantId,
            justification = "need it"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        return (json.GetProperty("id").GetGuid(), json.GetProperty("status").GetString()!);
    }

    // ----- who may use the template endpoints --------------------------------------------------

    [Fact]
    public async Task Anonymous_Gets401()
    {
        var templateId = await SeedTemplateAsync(await CreateTenantAsync());

        var statuses = await AllTemplateCallsAsync(_factory.CreateClient(), null, templateId);

        AssertAll(HttpStatusCode.Unauthorized, statuses);
    }

    [Fact]
    public async Task OrdinaryUser_Gets403_ForEveryCall_AndNothingChanges()
    {
        var tenant = await CreateTenantAsync();
        var tenantTemplate = await SeedTemplateAsync(tenant);
        var globalTemplate = await SeedTemplateAsync(null);
        var (userId, client) = await OrdinaryUserAsync();
        // Even a plain member of the tenant (no management role) is refused.
        await GrantRoleAsync(userId, "User", tenant);

        AssertAll(HttpStatusCode.Forbidden, await AllTemplateCallsAsync(client, tenant, tenantTemplate));
        AssertAll(HttpStatusCode.Forbidden, await AllTemplateCallsAsync(client, null, globalTemplate));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/workflow-templates?tenantId={tenant}")).StatusCode);

        Assert.Equal("seeded", (await TemplateRowAsync(tenantTemplate))!.Name);
        Assert.Equal("seeded", (await TemplateRowAsync(globalTemplate))!.Name);
    }

    [Fact]
    public async Task OrdinaryUser_CannotProbeWhichTemplateIdsExist()
    {
        var (_, client) = await OrdinaryUserAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/workflow-templates/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/workflow-templates/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task DeviceAndServiceAccountTokens_Get403()
    {
        var tenant = await CreateTenantAsync();
        var templateId = await SeedTemplateAsync(tenant);
        var device = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(Guid.NewGuid(), "dev-1", tenant));
        var service = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "workflows:write"));

        AssertAll(HttpStatusCode.Forbidden, await AllTemplateCallsAsync(device, tenant, templateId));
        AssertAll(HttpStatusCode.Forbidden, await AllTemplateCallsAsync(service, tenant, templateId));
        Assert.NotNull(await TemplateRowAsync(templateId));
    }

    [Fact]
    public async Task RoleClaimWithoutARoleRow_GrantsNothing()
    {
        var tenant = await CreateTenantAsync();
        var templateId = await SeedTemplateAsync(tenant);
        var client = ClientAs(await CreateUserAsync(), new[] { "BuildingManager", "TenantAdmin" });

        AssertAll(HttpStatusCode.Forbidden, await AllTemplateCallsAsync(client, tenant, templateId));
    }

    [Fact]
    public async Task ExpiredManagerRole_Gets403()
    {
        var tenant = await CreateTenantAsync();
        var templateId = await SeedTemplateAsync(tenant);
        var (_, client) = await UserWithRoleAsync("BuildingManager", tenant, expiresAt: DateTime.UtcNow.AddMinutes(-1));

        AssertAll(HttpStatusCode.Forbidden, await AllTemplateCallsAsync(client, tenant, templateId));
        Assert.Equal("seeded", (await TemplateRowAsync(templateId))!.Name);
    }

    // ----- tenant managers: their own tenant only ---------------------------------------------

    [Theory]
    [InlineData("BuildingManager")]
    [InlineData("BuildingOwner")]
    public async Task TenantManager_ManagesTheirOwnTenantsTemplates(string role)
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync(role, tenant);
        var name = $"own-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync("/api/workflow-templates", TemplateBody(tenant, name));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(tenant, (await TemplateRowAsync(id))!.TenantId);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/workflow-templates/{id}")).StatusCode);
        Assert.Contains(id, await TemplateIdsAsync(await client.GetAsync($"/api/workflow-templates?tenantId={tenant}")));
        var updated = await client.PutAsJsonAsync($"/api/workflow-templates/{id}", TemplateBody(tenant, name + "-v2"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(name + "-v2", (await TemplateRowAsync(id))!.Name);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/workflow-templates/{id}")).StatusCode);
        Assert.Null(await TemplateRowAsync(id));
    }

    [Fact]
    public async Task TenantManager_CannotTouchAnotherTenantsTemplates()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var foreign = await SeedTemplateAsync(other);
        var (_, client) = await UserWithRoleAsync("BuildingManager", own);
        var name = $"intruder-{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/workflow-templates", TemplateBody(other, name))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/workflow-templates/{foreign}", TemplateBody(other, "pwned"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/workflow-templates/{foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/workflow-templates/{foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/workflow-templates?tenantId={other}")).StatusCode);

        Assert.Empty(await TemplatesByNameAsync(name));
        Assert.Equal("seeded", (await TemplateRowAsync(foreign))!.Name);
    }

    [Fact]
    public async Task TenantManager_CannotManageGlobalTemplates_ButCanReadThem()
    {
        var tenant = await CreateTenantAsync();
        var global = await SeedTemplateAsync(null);
        var (_, client) = await UserWithRoleAsync("BuildingOwner", tenant);
        var name = $"global-{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/workflow-templates", TemplateBody(null, name))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/workflow-templates/{global}", TemplateBody(null, "pwned"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/workflow-templates/{global}")).StatusCode);
        Assert.Empty(await TemplatesByNameAsync(name));
        Assert.Equal("seeded", (await TemplateRowAsync(global))!.Name);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/workflow-templates/{global}")).StatusCode);
        Assert.Contains(global, await TemplateIdsAsync(await client.GetAsync($"/api/workflow-templates?tenantId={tenant}")));
    }

    [Fact]
    public async Task TenantManager_ListWithoutFilter_ShowsOwnAndGlobalTemplatesOnly()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var ownTemplate = await SeedTemplateAsync(own);
        var globalTemplate = await SeedTemplateAsync(null);
        var foreignTemplate = await SeedTemplateAsync(other);
        var (_, client) = await UserWithRoleAsync("BuildingManager", own);

        var ids = await TemplateIdsAsync(await client.GetAsync("/api/workflow-templates"));

        Assert.Contains(ownTemplate, ids);
        Assert.Contains(globalTemplate, ids);
        Assert.DoesNotContain(foreignTemplate, ids);
    }

    [Fact]
    public async Task TenantAdminRoleAlone_IsNotATemplateAdministrator()
    {
        // Same rule as invitations (TenantManagementAuthority): BuildingOwner / BuildingManager / SuperAdmin.
        var tenant = await CreateTenantAsync();
        var templateId = await SeedTemplateAsync(tenant);
        var (_, client) = await UserWithRoleAsync("TenantAdmin", tenant);

        AssertAll(HttpStatusCode.Forbidden, await AllTemplateCallsAsync(client, tenant, templateId));
    }

    // ----- SuperAdmin --------------------------------------------------------------------------

    [Fact]
    public async Task SuperAdmin_ManagesGlobalAndTenantTemplates()
    {
        var tenant = await CreateTenantAsync();
        var foreign = await SeedTemplateAsync(tenant);
        var client = await SuperAdminAsync();
        var name = $"global-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync("/api/workflow-templates", TemplateBody(null, name));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var globalId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Null((await TemplateRowAsync(globalId))!.TenantId);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/workflow-templates", TemplateBody(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/workflow-templates/{foreign}", TemplateBody(tenant, "edited"))).StatusCode);
        var all = await TemplateIdsAsync(await client.GetAsync("/api/workflow-templates"));
        Assert.Contains(globalId, all);
        Assert.Contains(foreign, all);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/workflow-templates/{globalId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/workflow-templates/{globalId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/workflow-templates/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Create_ForATenantThatDoesNotExist_Gets400()
    {
        var client = await SuperAdminAsync();
        var name = $"ghost-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/workflow-templates", TemplateBody(Guid.NewGuid(), name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await TemplatesByNameAsync(name));
    }

    // ----- auto-approve rules are checked on save ---------------------------------------------

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[{\"ResourceType\":\"role\"}]")]
    [InlineData("[{\"MaxPriority\":\"Critical\"}]")]
    [InlineData("[{\"MaxPriority\":\"nonsense\"}]")]
    [InlineData("[{\"RoleId\":\"00000000-0000-0000-0000-000000000000\"}]")]
    [InlineData("[{\"Unknown\":1}]")]
    [InlineData("[null]")]
    [InlineData("not json")]
    [InlineData("{\"RoleId\":\"x\"}")]
    public async Task AutoApproveRuleWithoutNarrowingCondition_IsRejectedOnSave(string rules)
    {
        var tenant = await CreateTenantAsync();
        var (_, manager) = await UserWithRoleAsync("BuildingManager", tenant);
        var admin = await SuperAdminAsync();
        var existing = await SeedTemplateAsync(tenant);
        var name = $"bad-{Guid.NewGuid():N}";

        foreach (var client in new[] { manager, admin })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/workflow-templates", TemplateBody(tenant, name, rules))).StatusCode);
            var update = await client.PutAsJsonAsync($"/api/workflow-templates/{existing}", TemplateBody(tenant, "edited", rules));
            Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
            Assert.Contains("auto-approve", await update.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await TemplatesByNameAsync(name));
        var row = await TemplateRowAsync(existing);
        Assert.Equal("seeded", row!.Name);
        Assert.Null(row.AutoApproveRules);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[{\"MaxPriority\":\"Normal\"}]")]
    [InlineData("[{\"maxPriority\":\"high\"}]")]
    public async Task AutoApproveRulesThatNarrowOrAreEmpty_AreAccepted(string? rules)
    {
        var tenant = await CreateTenantAsync();
        var (_, manager) = await UserWithRoleAsync("BuildingManager", tenant);

        var response = await manager.PostAsJsonAsync("/api/workflow-templates", TemplateBody(tenant, rules: rules));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AutoApproveRuleWithARole_IsAccepted_InPascalAndCamelCase()
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync();
        var (_, manager) = await UserWithRoleAsync("BuildingOwner", tenant);

        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync("/api/workflow-templates", TemplateBody(tenant, rules: RoleRule(role)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync("/api/workflow-templates",
            TemplateBody(tenant, rules: $"[{{\"roleId\":\"{role}\"}}]"))).StatusCode);
    }

    // ----- access requests: role and tenant must exist ----------------------------------------

    [Fact]
    public async Task AccessRequest_ForAnUnknownRoleOrTenant_IsRejected_AndNothingIsStored()
    {
        var tenant = await CreateTenantAsync();
        var inactive = await CreateTenantAsync(active: false);
        var role = await CreateRoleAsync();
        var (userId, client) = await OrdinaryUserAsync();

        async Task<HttpResponseMessage> Post(Guid? roleId, Guid? tenantId) => await client.PostAsJsonAsync("/api/access-requests",
            new { resourceType = "role", roleId, tenantId, justification = "x" });

        var unknownRole = await Post(Guid.NewGuid(), tenant);
        var unknownTenant = await Post(role, Guid.NewGuid());
        var inactiveTenant = await Post(role, inactive);

        foreach (var response in new[] { unknownRole, unknownTenant, inactiveTenant })
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("role", await unknownRole.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tenant", await unknownTenant.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        Assert.False(await db.AccessRequests.AnyAsync(r => r.RequesterId == userId));
    }

    [Fact]
    public async Task AccessRequest_ForAnExistingRoleAndTenant_StillWorks()
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync();
        var (_, client) = await OrdinaryUserAsync();

        var (id, status) = await FileRequestAsync(client, role, tenant);

        Assert.Equal("Pending", status);
        Assert.Equal(AccessRequestStatus.Pending, (await RequestRowAsync(id))!.Status);
    }

    // ----- approval time: a rule without a narrowing condition never approves ------------------

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[{\"ResourceType\":\"role\"}]")]
    [InlineData("[{\"MaxPriority\":\"Critical\"}]")]
    [InlineData("[{\"Unknown\":1}]")]
    public async Task StoredAutoApproveRuleWithoutNarrowingCondition_IsIgnoredWhenARequestIsMatched(string rules)
    {
        // Seeded straight into the database: a template stored before the rule was refused on save.
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync();
        await SeedTemplateAsync(tenant, autoApproveRules: rules);
        var (_, client) = await OrdinaryUserAsync();

        var (id, status) = await FileRequestAsync(client, role, tenant);

        Assert.Equal("Pending", status);
        Assert.Equal(AccessRequestStatus.Pending, (await RequestRowAsync(id))!.Status);
    }

    [Fact]
    public async Task StoredGlobalWildcardRule_IsIgnoredToo()
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync();
        await SeedTemplateAsync(null, resourceType: "role", autoApproveRules: "[{}]");
        var (_, client) = await OrdinaryUserAsync();

        var (_, status) = await FileRequestAsync(client, role, tenant);

        Assert.Equal("Pending", status);
    }

    [Fact]
    public async Task StoredMixedRules_OnlyTheNarrowingRuleCanApprove()
    {
        var tenant = await CreateTenantAsync();
        var allowedRole = await CreateRoleAsync();
        var otherRole = await CreateRoleAsync();
        await SeedTemplateAsync(tenant, autoApproveRules: $"[{{}},{{\"RoleId\":\"{allowedRole}\"}}]");
        var (_, client) = await OrdinaryUserAsync();

        Assert.Equal("Pending", (await FileRequestAsync(client, otherRole, tenant)).Status);
        Assert.Equal("Approved", (await FileRequestAsync(client, allowedRole, tenant)).Status);
    }

    [Fact]
    public async Task LegitimateTenantTemplate_WithARoleRule_StillAutoApprovesThatRoleOnly()
    {
        var tenant = await CreateTenantAsync();
        var allowedRole = await CreateRoleAsync();
        var otherRole = await CreateRoleAsync();
        var (_, manager) = await UserWithRoleAsync("BuildingManager", tenant);
        var (_, user) = await OrdinaryUserAsync();
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync("/api/workflow-templates",
            TemplateBody(tenant, rules: RoleRule(allowedRole)))).StatusCode);

        var approved = await FileRequestAsync(user, allowedRole, tenant);
        var pending = await FileRequestAsync(user, otherRole, tenant);

        Assert.Equal("Approved", approved.Status);
        Assert.Equal(AccessRequestStatus.Approved, (await RequestRowAsync(approved.RequestId))!.Status);
        Assert.Equal("Pending", pending.Status);
    }

    [Fact]
    public async Task StoredCamelCaseRoleRule_NowNarrowsInsteadOfMatchingEverything()
    {
        // Before the fix property names were case-sensitive, so a camelCase rule was an empty rule matching everything.
        var tenant = await CreateTenantAsync();
        var allowedRole = await CreateRoleAsync();
        var otherRole = await CreateRoleAsync();
        await SeedTemplateAsync(tenant, autoApproveRules: $"[{{\"roleId\":\"{allowedRole}\"}}]");
        var (_, client) = await OrdinaryUserAsync();

        Assert.Equal("Pending", (await FileRequestAsync(client, otherRole, tenant)).Status);
        Assert.Equal("Approved", (await FileRequestAsync(client, allowedRole, tenant)).Status);
    }

    // ----- the attack from the finding, end to end ---------------------------------------------

    [Fact]
    public async Task AttackSequence_OrdinaryUserCannotGetARequestApproved()
    {
        var victimTenant = await CreateTenantAsync();
        var targetRole = await CreateRoleAsync();
        var (attackerId, attacker) = await OrdinaryUserAsync();
        var name = $"attack-{Guid.NewGuid():N}";

        // 1. create a match-everything auto-approve template for the victim tenant, and a global one
        var forTenant = await attacker.PostAsJsonAsync("/api/workflow-templates", TemplateBody(victimTenant, name, "[{}]"));
        var global = await attacker.PostAsJsonAsync("/api/workflow-templates", TemplateBody(null, name, "[{}]"));
        // 2. file a request for any role in that tenant
        var (requestId, status) = await FileRequestAsync(attacker, targetRole, victimTenant);

        Assert.Equal(HttpStatusCode.Forbidden, forTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, global.StatusCode);
        Assert.Empty(await TemplatesByNameAsync(name));
        Assert.Equal("Pending", status);
        Assert.Equal(AccessRequestStatus.Pending, (await RequestRowAsync(requestId))!.Status);
        Assert.Equal(attackerId, (await RequestRowAsync(requestId))!.RequesterId);
    }

    [Fact]
    public async Task AttackSequence_ATenantManagerCannotSaveTheWildcardEither()
    {
        var tenant = await CreateTenantAsync();
        var role = await CreateRoleAsync();
        var (_, manager) = await UserWithRoleAsync("BuildingManager", tenant);
        var (_, attacker) = await OrdinaryUserAsync();
        var name = $"wild-{Guid.NewGuid():N}";

        var save = await manager.PostAsJsonAsync("/api/workflow-templates", TemplateBody(tenant, name, "[{}]"));
        var (_, status) = await FileRequestAsync(attacker, role, tenant);

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Empty(await TemplatesByNameAsync(name));
        Assert.Equal("Pending", status);
    }
}
