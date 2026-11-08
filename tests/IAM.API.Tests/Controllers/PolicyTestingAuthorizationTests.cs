using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Configuration;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5486: every Policy Testing API action needs admin rights in the tenant of the policy under test (a role claim
/// alone, a row in another tenant or no row confers nothing), lists show only the caller's tenants, a run is capped,
/// and a failed scenario never returns a stack trace.
/// </summary>
public class PolicyTestingAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public PolicyTestingAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------

    private static async Task<Guid> CreateTenantAsync(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task<Guid> CreateUserAsync(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@policy-testing.test", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task GrantRoleAsync(IServiceProvider sp, Guid userId, string roleName, Guid tenantId)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role == null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName };
            db.Roles.Add(role);
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenantId, GrantedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedPolicyAsync(IServiceProvider sp, Guid tenantId)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var policy = new Policy
        {
            Id = Guid.NewGuid(), Name = $"p-{Guid.NewGuid():N}"[..12], TenantId = tenantId,
            Resource = "doors:*", Action = "open", Effect = PolicyEffect.Allow, IsActive = true,
            CreatedByUserId = Guid.NewGuid()
        };
        db.Policies.Add(policy);
        await db.SaveChangesAsync();
        return policy.Id;
    }

    private static async Task<Guid> SeedTestAsync(IServiceProvider sp, Guid policyId, Guid tenantId, int scenarios = 1)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var list = Enumerable.Range(0, scenarios)
            .Select(i => new { Name = $"s{i}", UserId = Guid.NewGuid(), Resource = "doors:1", Action = "open" });
        var test = new PolicyTest
        {
            Id = Guid.NewGuid(), Name = $"t-{Guid.NewGuid():N}"[..12], PolicyId = policyId, TenantId = tenantId,
            TestScenarios = JsonSerializer.Serialize(list), ExpectedResults = "[\"allow\"]",
            CreatedByUserId = Guid.NewGuid()
        };
        db.PolicyTests.Add(test);
        await db.SaveChangesAsync();
        return test.Id;
    }

    private static HttpClient ClientAs(WebApplicationFactory<Program> factory, Guid userId, params string[] roleClaims)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@policy-testing.test", roleClaims, null));
        return client;
    }

    private static async Task<HttpClient> TenantAdminOfAsync(WebApplicationFactory<Program> factory, Guid tenantId)
    {
        var userId = await CreateUserAsync(factory.Services);
        await GrantRoleAsync(factory.Services, userId, "TenantAdmin", tenantId);
        return ClientAs(factory, userId, "TenantAdmin");
    }

    private static object CreateBody(Guid policyId, Guid tenantId) => new
    {
        name = "t", description = "d", policyId, policyVersion = "1", testScenario = "doors:1",
        expectedResult = "allow", tenantId
    };

    private sealed record World(Guid TenantA, Guid TenantB, Guid PolicyA, Guid TestA, Guid TestB, Guid PolicyB);

    private async Task<World> SeedWorldAsync()
    {
        var sp = _factory.Services;
        var a = await CreateTenantAsync(sp);
        var b = await CreateTenantAsync(sp);
        var pa = await SeedPolicyAsync(sp, a);
        var pb = await SeedPolicyAsync(sp, b);
        return new World(a, b, pa, await SeedTestAsync(sp, pa, a), await SeedTestAsync(sp, pb, b), pb);
    }

    /// <summary>Every action of the API, as (name, request) for the given world, run against policy/test of tenant A.</summary>
    private static IEnumerable<(string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)> Actions(World w) => new (string, Func<HttpClient, Task<HttpResponseMessage>>)[]
    {
        ("create", c => c.PostAsJsonAsync("/api/policytesting/tests", CreateBody(w.PolicyA, w.TenantA))),
        ("get", c => c.GetAsync($"/api/policytesting/tests/{w.TestA}")),
        ("execute", c => c.PostAsync($"/api/policytesting/tests/{w.TestA}/execute", null)),
        ("execute-tests", c => c.PostAsync($"/api/policytesting/policies/{w.PolicyA}/execute-tests", null)),
        ("coverage", c => c.GetAsync($"/api/policytesting/policies/{w.PolicyA}/coverage")),
        ("list", c => c.GetAsync("/api/policytesting/tests")),
        ("compare", c => c.GetAsync($"/api/policytesting/policies/{w.PolicyA}/compare?version1=1&version2=2")),
    };

    // ----- refusals --------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_IsUnauthorized_OnEveryAction()
    {
        var w = await SeedWorldAsync();
        foreach (var (name, call) in Actions(w))
        {
            var response = await call(_factory.CreateClient());
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task OrdinaryUser_IsForbidden_OnEveryAction()
    {
        var w = await SeedWorldAsync();
        var userId = await CreateUserAsync(_factory.Services);
        await GrantRoleAsync(_factory.Services, userId, "User", w.TenantA);
        // Claims an admin role it does not hold in any tenant: the claim alone confers nothing.
        var client = ClientAs(_factory, userId, "TenantAdmin");

        foreach (var (name, call) in Actions(w))
        {
            var response = await call(client);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task AdminOfAnotherTenant_IsForbidden_OnEveryAction()
    {
        var w = await SeedWorldAsync();
        var client = await TenantAdminOfAsync(_factory, w.TenantB);

        foreach (var (name, call) in Actions(w).Where(a => a.Name != "list"))
        {
            var response = await call(client);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: {response.StatusCode}");
        }
    }

    // ----- allowed ---------------------------------------------------------------------------

    [Fact]
    public async Task AdminOfTheTenant_CanUseEveryAction()
    {
        var w = await SeedWorldAsync();
        var client = await TenantAdminOfAsync(_factory, w.TenantA);

        foreach (var (name, call) in Actions(w))
        {
            var response = await call(client);
            Assert.True(response.IsSuccessStatusCode, $"{name}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task PlatformAdmin_CanUseEveryAction_OnAnyTenant()
    {
        var w = await SeedWorldAsync();
        var client = ClientAs(_factory, Guid.NewGuid(), "SuperAdmin");

        foreach (var (name, call) in Actions(w))
        {
            var response = await call(client);
            Assert.True(response.IsSuccessStatusCode, $"{name}: {response.StatusCode}");
        }
    }

    // ----- tenant pinning --------------------------------------------------------------------

    [Fact]
    public async Task List_ShowsOnlyTestsOfAdministeredTenants()
    {
        var w = await SeedWorldAsync();
        var client = await TenantAdminOfAsync(_factory, w.TenantA);

        var response = await client.GetAsync("/api/policytesting/tests");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

        Assert.Contains(w.TestA, ids);
        Assert.DoesNotContain(w.TestB, ids);

        // Asking for the other tenant's policy shows nothing rather than its tests.
        var other = await client.GetAsync($"/api/policytesting/tests?policyId={w.PolicyB}");
        Assert.Empty((await other.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Create_WithTenantIdOfAnotherTenant_IsRefused_AndNothingStored()
    {
        var w = await SeedWorldAsync();
        // Admin of both tenants: the mismatch alone must be refused.
        var userId = await CreateUserAsync(_factory.Services);
        await GrantRoleAsync(_factory.Services, userId, "TenantAdmin", w.TenantA);
        await GrantRoleAsync(_factory.Services, userId, "TenantAdmin", w.TenantB);
        var client = ClientAs(_factory, userId, "TenantAdmin");

        var response = await client.PostAsJsonAsync("/api/policytesting/tests", CreateBody(w.PolicyA, w.TenantB));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // An admin of B only cannot create against A's policy at all.
        var onlyB = await TenantAdminOfAsync(_factory, w.TenantB);
        var forbidden = await onlyB.PostAsJsonAsync("/api/policytesting/tests", CreateBody(w.PolicyA, w.TenantB));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // ----- cap and error detail --------------------------------------------------------------

    private sealed class ThrowingEngine : IPolicyInheritanceEngine
    {
        public Task<List<Policy>> GetEffectivePoliciesForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret-detail");
        public Task<List<Guid>> GetInheritedByTenantsAsync(Guid policyId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret-detail");
        public Task<PolicyEvaluationResult> EvaluateAsync(Guid userId, Guid tenantId, string resource, string action,
            PolicyEvaluationContext? context = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret-detail");
        public Task<PolicyImpactAnalysis> SimulatePolicyImpactAsync(Policy policy, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret-detail");
    }

    [Fact]
    public async Task Run_ExecutesAtMostTheConfiguredNumberOfTests()
    {
        await using var factory = new IAMTestWebApplicationFactory();
        await using var capped = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Configure<PolicyTestingOptions>(o => o.MaxTestsPerRun = 2)));
        await capped.EnsureSeededAsyncIfPossible();

        var tenant = await CreateTenantAsync(capped.Services);
        var policy = await SeedPolicyAsync(capped.Services, tenant);
        for (var i = 0; i < 5; i++)
            await SeedTestAsync(capped.Services, policy, tenant);

        var client = ClientAs(capped, Guid.NewGuid(), "SuperAdmin");
        var response = await client.PostAsync($"/api/policytesting/policies/{policy}/execute-tests", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }

    [Fact]
    public async Task FailedScenario_ReturnsNoStackTraceOrExceptionType()
    {
        await using var factory = new IAMTestWebApplicationFactory();
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddScoped<IPolicyInheritanceEngine, ThrowingEngine>()));
        await failing.EnsureSeededAsyncIfPossible();

        var tenant = await CreateTenantAsync(failing.Services);
        var policy = await SeedPolicyAsync(failing.Services, tenant);
        var test = await SeedTestAsync(failing.Services, policy, tenant);

        var client = ClientAs(failing, Guid.NewGuid(), "SuperAdmin");
        var response = await client.PostAsync($"/api/policytesting/tests/{test}/execute", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("detailedResults", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-detail", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain(" at IAM.", body);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class FactoryExtensions
{
    /// <summary>The derived factory builds its host (and seeds) on first use of Services.</summary>
    public static Task EnsureSeededAsyncIfPossible(this WebApplicationFactory<Program> factory)
    {
        _ = factory.Services;
        return Task.CompletedTask;
    }
}
