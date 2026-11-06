using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>Task 5573: the authorization decision cache key must include the request context.</summary>
public class UnifiedAuthorizationCacheContextTests
{
    private sealed class IpPolicyEngine : IPolicyInheritanceEngine
    {
        public int Calls;

        public Task<PolicyEvaluationResult> EvaluateAsync(Guid userId, Guid tenantId, string resource, string action,
            PolicyEvaluationContext? context = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PolicyEvaluationResult
            {
                IsAllowed = context?.IpAddress == "10.0.0.5",
                MatchedPolicy = new Policy { Name = "internal-ip-only" },
                Reason = "requires internal ip"
            });
        }

        public Task<List<Policy>> GetEffectivePoliciesForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<Guid>> GetInheritedByTenantsAsync(Guid policyId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PolicyImpactAnalysis> SimulatePolicyImpactAsync(Policy policy, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private static (UnifiedAuthorizationService Svc, IpPolicyEngine Engine) Create()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var engine = new IpPolicyEngine();
        var svc = new UnifiedAuthorizationService(new IAMDbContext(options), engine,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<UnifiedAuthorizationService>.Instance);
        return (svc, engine);
    }

    private static AuthorizationRequest Req(Dictionary<string, object>? ctx) => new()
    {
        PrincipalType = "user",
        PrincipalId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Resource = "/api/things",
        Action = "read",
        Context = ctx
    };

    [Fact]
    public async Task Allow_with_satisfying_context_is_not_served_to_failing_or_missing_context()
    {
        var (svc, engine) = Create();

        var good = await svc.EvaluateAsync(Req(new() { ["ip_address"] = "10.0.0.5" }));
        Assert.True(good.Allowed);

        var bad = await svc.EvaluateAsync(Req(new() { ["ip_address"] = "203.0.113.9" }));
        var none = await svc.EvaluateAsync(Req(null));

        Assert.False(bad.Allowed);
        Assert.False(none.Allowed);
        Assert.Equal(3, engine.Calls);
    }

    [Fact]
    public async Task Identical_requests_hit_the_cache_without_mutating_the_stored_decision()
    {
        var (svc, engine) = Create();

        var first = await svc.EvaluateAsync(Req(new() { ["ip_address"] = "10.0.0.5", ["mfa"] = true }));
        var pathBefore = first.EvaluationPath.ToList();
        // same content, different key order
        var second = await svc.EvaluateAsync(Req(new() { ["mfa"] = true, ["ip_address"] = "10.0.0.5" }));

        Assert.Equal(1, engine.Calls);
        Assert.Contains(second.EvaluationPath, p => p.Contains("Cache hit"));
        Assert.NotSame(first, second);
        Assert.Equal(pathBefore, first.EvaluationPath);
        Assert.DoesNotContain(first.EvaluationPath, p => p.Contains("Cache hit"));
    }

    [Fact]
    public void Fingerprint_is_order_independent_and_value_sensitive()
    {
        var a = UnifiedAuthorizationService.ContextFingerprint(new() { ["a"] = "1", ["b"] = "2" });
        var b = UnifiedAuthorizationService.ContextFingerprint(new() { ["b"] = "2", ["a"] = "1" });
        var c = UnifiedAuthorizationService.ContextFingerprint(new() { ["a"] = "1", ["b"] = "3" });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
