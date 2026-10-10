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
/// Task 5149: every Policy API action is decided from the caller's UserRoles in the tenant the policy belongs to.
/// Managing needs SuperAdmin or an active BuildingOwner/BuildingManager row in that tenant (a role claim alone, a row
/// in another tenant, an expired row or no row confers nothing), reading needs membership of the tenant (or of a
/// tenant that inherits the policy), and a policy of another tenant looks like an unknown id.
/// </summary>
public class PolicyAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public PolicyAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------

    private async Task<Guid> CreateTenantAsync(Guid? parentId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true, ParentTenantId = parentId };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@policy-authz.test", PasswordHash = "x" };
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
            role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName };
            db.Roles.Add(role);
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenantId,
            GrantedAt = DateTime.UtcNow, ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedPolicyAsync(Guid tenantId, InheritanceScope scope = InheritanceScope.Self, bool active = true)
    {
        using var scoped = _factory.Services.CreateScope();
        var db = scoped.ServiceProvider.GetRequiredService<IAMDbContext>();
        var policy = new Policy
        {
            Id = Guid.NewGuid(), Name = $"p-{Guid.NewGuid():N}"[..12], TenantId = tenantId, InheritanceScope = scope,
            Resource = "doors:*", Action = "open", Effect = PolicyEffect.Allow, IsActive = active,
            CreatedByUserId = Guid.NewGuid()
        };
        db.Policies.Add(policy);
        await db.SaveChangesAsync();
        return policy.Id;
    }

    private async Task<Policy?> PolicyRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    private HttpClient ClientAs(Guid userId, params string[] roleClaims)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@policy-authz.test", roleClaims, null));
        return client;
    }

    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), "SuperAdmin");

    /// <summary>A user holding the role in the tenant; the token carries the same role claim.</summary>
    private async Task<HttpClient> WithRoleAsync(string roleName, Guid tenantId, DateTime? expiresAt = null)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, roleName, tenantId, expiresAt);
        return ClientAs(userId, roleName);
    }

    /// <summary>Carries the BuildingManager role CLAIM but only a plain member row: a global role name confers nothing.</summary>
    private async Task<HttpClient> ClaimOnlyManagerAsync(Guid memberOfTenantId)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "User", memberOfTenantId);
        return ClientAs(userId, "BuildingManager");
    }

    /// <summary>Carries the BuildingManager claim and belongs to no tenant at all.</summary>
    private async Task<HttpClient> OutsiderClaimingManagerAsync() => ClientAs(await CreateUserAsync(), "BuildingManager");

    private static object CreateBody(Guid tenantId) => new
    {
        name = "new policy", tenantId, inheritanceScope = "Descendants", resource = "doors:*", action = "open", effect = "Deny", priority = 100
    };

    private static object UpdateBody(bool? isActive = false) => new { name = "renamed", priority = 5, isActive };

    private static object SimulateBody(Guid tenantId) => new
    {
        tenantId, inheritanceScope = "Descendants", resource = "doors:*", action = "open", effect = "Deny"
    };

    private static async Task<List<Guid>> ListedIdsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    // ----- create ----------------------------------------------------------------------------

    [Fact]
    public async Task Create_SuperAdmin_OwnerAndManagerOfTheTenant_Succeed()
    {
        var tenant = await CreateTenantAsync();

        Assert.Equal(HttpStatusCode.Created, (await SuperAdmin().PostAsJsonAsync("/api/policies", CreateBody(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await (await WithRoleAsync("BuildingOwner", tenant)).PostAsJsonAsync("/api/policies", CreateBody(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await (await WithRoleAsync("BuildingManager", tenant)).PostAsJsonAsync("/api/policies", CreateBody(tenant))).StatusCode);
    }

    [Fact]
    public async Task Create_IsRefused_ForOtherTenantManager_PlainMember_ClaimOnlyManager_Outsider_AndExpiredRole()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var callers = new Dictionary<string, HttpClient>
        {
            ["manager of another tenant"] = await WithRoleAsync("BuildingManager", tenantB),
            ["owner of another tenant"] = await WithRoleAsync("BuildingOwner", tenantB),
            ["plain member"] = await WithRoleAsync("User", tenantA),
            ["role claim only"] = await ClaimOnlyManagerAsync(tenantA),
            ["outsider"] = await OutsiderClaimingManagerAsync(),
            ["expired manager"] = await WithRoleAsync("BuildingManager", tenantA, DateTime.UtcNow.AddMinutes(-5)),
        };

        foreach (var (who, client) in callers)
            Assert.True(HttpStatusCode.Forbidden == (await client.PostAsJsonAsync("/api/policies", CreateBody(tenantA))).StatusCode, who);

        using var scope = _factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IAMDbContext>().Policies.AnyAsync(p => p.TenantId == tenantA));
    }

    [Fact]
    public async Task Create_ForAnUnknownTenant_IsRefusedToANonSuperAdmin_SoItRevealsNothing()
    {
        var manager = await WithRoleAsync("BuildingManager", await CreateTenantAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/policies", CreateBody(Guid.NewGuid()))).StatusCode);
    }

    // ----- update ----------------------------------------------------------------------------

    [Fact]
    public async Task Update_SuperAdmin_AndManagerOfThePoliciesTenant_Succeed()
    {
        var tenant = await CreateTenantAsync();
        var policyId = await SeedPolicyAsync(tenant);

        Assert.Equal(HttpStatusCode.OK, (await (await WithRoleAsync("BuildingManager", tenant)).PutAsJsonAsync($"/api/policies/{policyId}", UpdateBody(true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().PutAsJsonAsync($"/api/policies/{policyId}", UpdateBody(true))).StatusCode);
        Assert.Equal("renamed", (await PolicyRowAsync(policyId))!.Name);
    }

    [Fact]
    public async Task Update_ByAManagerOfAnotherTenant_LooksLikeAnUnknownId_AndCannotDeactivateThePolicy()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var policyId = await SeedPolicyAsync(tenantA);
        var otherManager = await WithRoleAsync("BuildingManager", tenantB);

        var response = await otherManager.PutAsJsonAsync($"/api/policies/{policyId}", UpdateBody(false));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // the same answer as for an id that does not exist
        Assert.Equal(HttpStatusCode.NotFound, (await otherManager.PutAsJsonAsync($"/api/policies/{Guid.NewGuid()}", UpdateBody(false))).StatusCode);
        var row = await PolicyRowAsync(policyId);
        Assert.True(row!.IsActive);
        Assert.NotEqual("renamed", row.Name);
    }

    [Fact]
    public async Task Update_IsRefused_ForPlainMember_ClaimOnlyManager_Outsider_AndExpiredRole()
    {
        var tenant = await CreateTenantAsync();
        var policyId = await SeedPolicyAsync(tenant);
        var callers = new Dictionary<string, HttpClient>
        {
            ["plain member"] = await WithRoleAsync("User", tenant),
            ["role claim only"] = await ClaimOnlyManagerAsync(tenant),
            ["outsider"] = await OutsiderClaimingManagerAsync(),
            ["expired manager"] = await WithRoleAsync("BuildingManager", tenant, DateTime.UtcNow.AddMinutes(-5)),
        };

        foreach (var (who, client) in callers)
            Assert.True(HttpStatusCode.Forbidden == (await client.PutAsJsonAsync($"/api/policies/{policyId}", UpdateBody(false))).StatusCode, who);

        Assert.True((await PolicyRowAsync(policyId))!.IsActive);
    }

    // ----- simulate --------------------------------------------------------------------------

    [Fact]
    public async Task Simulate_NeedsToManageTheTenantInTheBody()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().PostAsJsonAsync("/api/policies/simulate", SimulateBody(tenantA))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await WithRoleAsync("BuildingOwner", tenantA)).PostAsJsonAsync("/api/policies/simulate", SimulateBody(tenantA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await WithRoleAsync("BuildingOwner", tenantB)).PostAsJsonAsync("/api/policies/simulate", SimulateBody(tenantA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await WithRoleAsync("BuildingOwner", tenantA, DateTime.UtcNow.AddMinutes(-5))).PostAsJsonAsync("/api/policies/simulate", SimulateBody(tenantA))).StatusCode);
        // a claim-only owner (plain member row) and an outsider
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "User", tenantA);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(userId, "BuildingOwner").PostAsJsonAsync("/api/policies/simulate", SimulateBody(tenantA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(await CreateUserAsync(), "BuildingOwner").PostAsJsonAsync("/api/policies/simulate", SimulateBody(tenantA))).StatusCode);
    }

    // ----- list ------------------------------------------------------------------------------

    [Fact]
    public async Task List_ShowsOnlyThePoliciesOfTenantsTheCallerBelongsTo()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var inA = await SeedPolicyAsync(tenantA);
        var inB = await SeedPolicyAsync(tenantB);

        var member = await WithRoleAsync("User", tenantA);
        var ids = await ListedIdsAsync(await member.GetAsync("/api/policies"));
        Assert.Contains(inA, ids);
        Assert.DoesNotContain(inB, ids);

        // asking for another tenant explicitly shows nothing
        Assert.Empty(await ListedIdsAsync(await member.GetAsync($"/api/policies?tenantId={tenantB}")));

        var all = await ListedIdsAsync(await SuperAdmin().GetAsync("/api/policies"));
        Assert.Contains(inA, all);
        Assert.Contains(inB, all);
    }

    [Fact]
    public async Task List_ShowsNothingToAnOutsider_OrAnExpiredRole_AndRefusesServiceAccountTokens()
    {
        var tenant = await CreateTenantAsync();
        var policyId = await SeedPolicyAsync(tenant);

        var outsider = ClientAs(await CreateUserAsync(), "User");
        Assert.DoesNotContain(policyId, await ListedIdsAsync(await outsider.GetAsync("/api/policies")));

        var expired = await WithRoleAsync("User", tenant, DateTime.UtcNow.AddMinutes(-5));
        Assert.DoesNotContain(policyId, await ListedIdsAsync(await expired.GetAsync("/api/policies")));

        var service = _factory.CreateClient();
        service.AddAuthorizationHeader(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "policies:read"));
        Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync("/api/policies")).StatusCode);
    }

    // ----- get -------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsThePolicyToAMemberOfItsTenant_AndToSuperAdmin()
    {
        var tenant = await CreateTenantAsync();
        var policyId = await SeedPolicyAsync(tenant);

        Assert.Equal(HttpStatusCode.OK, (await (await WithRoleAsync("User", tenant)).GetAsync($"/api/policies/{policyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().GetAsync($"/api/policies/{policyId}")).StatusCode);
    }

    [Fact]
    public async Task Get_LooksLikeAnUnknownId_ToOtherTenantMembers_Outsiders_AndExpiredRoles()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var policyId = await SeedPolicyAsync(tenantA);

        Assert.Equal(HttpStatusCode.NotFound, (await (await WithRoleAsync("BuildingManager", tenantB)).GetAsync($"/api/policies/{policyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClientAs(await CreateUserAsync(), "User").GetAsync($"/api/policies/{policyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await WithRoleAsync("User", tenantA, DateTime.UtcNow.AddMinutes(-5))).GetAsync($"/api/policies/{policyId}")).StatusCode);
    }

    [Fact]
    public async Task Get_AParentTenantsInheritedPolicy_StaysReadableForChildTenantMembers_ButNotASelfScopedOne()
    {
        var parent = await CreateTenantAsync();
        var child = await CreateTenantAsync(parent);
        var inherited = await SeedPolicyAsync(parent, InheritanceScope.Children);
        var selfOnly = await SeedPolicyAsync(parent, InheritanceScope.Self);
        var childMember = await WithRoleAsync("User", child);

        Assert.Equal(HttpStatusCode.OK, (await childMember.GetAsync($"/api/policies/{inherited}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await childMember.GetAsync($"/api/policies/{selfOnly}")).StatusCode);
    }

    // ----- effective -------------------------------------------------------------------------

    [Fact]
    public async Task Effective_IsForMembers_AndIncludesTheParentsInheritedPolicies()
    {
        var parent = await CreateTenantAsync();
        var child = await CreateTenantAsync(parent);
        var other = await CreateTenantAsync();
        var inherited = await SeedPolicyAsync(parent, InheritanceScope.Descendants);
        var own = await SeedPolicyAsync(child);
        var childMember = await WithRoleAsync("User", child);

        var response = await childMember.GetAsync($"/api/policies/tenant/{child}/effective");

        var ids = await ListedIdsAsync(response);
        Assert.Contains(inherited, ids);
        Assert.Contains(own, ids);
        Assert.Equal(HttpStatusCode.Forbidden, (await childMember.GetAsync($"/api/policies/tenant/{parent}/effective")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await childMember.GetAsync($"/api/policies/tenant/{other}/effective")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(await CreateUserAsync(), "User").GetAsync($"/api/policies/tenant/{child}/effective")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await WithRoleAsync("User", child, DateTime.UtcNow.AddMinutes(-5))).GetAsync($"/api/policies/tenant/{child}/effective")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().GetAsync($"/api/policies/tenant/{other}/effective")).StatusCode);
    }

    // ----- inherited-by ----------------------------------------------------------------------

    [Fact]
    public async Task InheritedBy_NamesOnlyTenantsTheCallerBelongsTo_AndHidesOtherTenantsPolicies()
    {
        var parent = await CreateTenantAsync();
        var childOne = await CreateTenantAsync(parent);
        var childTwo = await CreateTenantAsync(parent);
        var policyId = await SeedPolicyAsync(parent, InheritanceScope.Children);

        var childOneMember = await WithRoleAsync("User", childOne);
        var response = await childOneMember.GetAsync($"/api/policies/{policyId}/inherited-by");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tenants = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { childOne }, tenants);

        var all = (await (await SuperAdmin().GetAsync($"/api/policies/{policyId}/inherited-by")).Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(childTwo, all);

        var unrelated = await WithRoleAsync("User", await CreateTenantAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await unrelated.GetAsync($"/api/policies/{policyId}/inherited-by")).StatusCode);
    }

    // ----- evaluate --------------------------------------------------------------------------

    [Fact]
    public async Task Evaluate_WorksForMembers_AndCannotProbeATenantTheCallerDoesNotBelongTo()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        await SeedPolicyAsync(tenantB);
        var member = await WithRoleAsync("User", tenantA);
        object Body(Guid tenantId) => new { tenantId, resource = "doors:front", action = "open" };

        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync("/api/policies/evaluate", Body(tenantA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/policies/evaluate", Body(tenantB))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/policies/evaluate", Body(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(await CreateUserAsync(), "User").PostAsJsonAsync("/api/policies/evaluate", Body(tenantA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await WithRoleAsync("User", tenantA, DateTime.UtcNow.AddMinutes(-5))).PostAsJsonAsync("/api/policies/evaluate", Body(tenantA))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().PostAsJsonAsync("/api/policies/evaluate", Body(tenantB))).StatusCode);
    }
}
