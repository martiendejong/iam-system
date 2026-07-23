using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Implementation of policy inheritance engine with caching and optimization.
/// Uses hierarchical tree traversal with O(log n) performance through caching.
/// </summary>
public class PolicyInheritanceEngine : IPolicyInheritanceEngine
{
    private readonly IAMDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PolicyInheritanceEngine> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);

    public PolicyInheritanceEngine(
        IAMDbContext context,
        IMemoryCache cache,
        ILogger<PolicyInheritanceEngine> logger,
        IServiceScopeFactory scopeFactory)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    // EvaluateAsync runs on every authorization decision, so publishing must never block it on
    // webhook HTTP delivery/retry backoff. It also outlives the caller's request scope, so it
    // resolves its own IEventBus from a fresh scope instead of reusing the (soon-disposed) one.
    private void PublishPolicyEvaluatedEvent(Guid userId, Guid tenantId, string resource, string action, PolicyEvaluationResult result)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                await eventBus.PublishAsync(IamEventTypes.PolicyEvaluated, new
                {
                    userId,
                    tenantId,
                    resource,
                    action,
                    isAllowed = result.IsAllowed,
                    policyId = result.PolicyId,
                    evaluatedPoliciesCount = result.EvaluatedPoliciesCount,
                    evaluationTimeMs = result.EvaluationTimeMs
                }, tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish policy.evaluated event for user {UserId} tenant {TenantId}", userId, tenantId);
            }
        });
    }

    public async Task<List<Policy>> GetEffectivePoliciesForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"effective_policies_{tenantId}";

        if (_cache.TryGetValue(cacheKey, out List<Policy>? cachedPolicies) && cachedPolicies != null)
        {
            _logger.LogDebug("Cache hit for effective policies: {TenantId}", tenantId);
            return cachedPolicies;
        }

        _logger.LogDebug("Cache miss, computing effective policies for: {TenantId}", tenantId);

        // Get tenant hierarchy (self + all ancestors)
        var hierarchyPath = await GetTenantHierarchyPathAsync(tenantId, cancellationToken);

        // Collect policies from self and all ancestors
        var effectivePolicies = new List<Policy>();

        foreach (var ancestorId in hierarchyPath)
        {
            // Get policies defined at this level
            var policiesAtLevel = await _context.Policies
                .Include(p => p.Tenant)
                .Include(p => p.Role)
                .Where(p => p.TenantId == ancestorId && p.IsActive)
                .Where(p => p.ExpiresAt == null || p.ExpiresAt > DateTime.UtcNow)
                .ToListAsync(cancellationToken);

            // Filter policies that apply to descendants
            var applicablePolicies = policiesAtLevel.Where(p =>
            {
                // If this is the target tenant itself, all policies apply
                if (ancestorId == tenantId)
                    return true;

                // Otherwise, check if policy inherits to descendants
                var depth = hierarchyPath.IndexOf(tenantId) - hierarchyPath.IndexOf(ancestorId);

                return p.InheritanceScope switch
                {
                    InheritanceScope.Children => depth == 1,
                    InheritanceScope.Descendants => depth > 0,
                    _ => false
                };
            }).ToList();

            effectivePolicies.AddRange(applicablePolicies);
        }

        // Sort by priority (highest first), then by specificity (most specific tenant first)
        var sortedPolicies = effectivePolicies
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => hierarchyPath.IndexOf(p.TenantId)) // More specific tenants come first
            .ToList();

        // Cache the result
        _cache.Set(cacheKey, sortedPolicies, _cacheExpiration);

        _logger.LogInformation(
            "Computed {Count} effective policies for tenant {TenantId}",
            sortedPolicies.Count,
            tenantId);

        return sortedPolicies;
    }

    public async Task<List<Guid>> GetInheritedByTenantsAsync(
        Guid policyId,
        CancellationToken cancellationToken = default)
    {
        var policy = await _context.Policies
            .Include(p => p.Tenant)
            .FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);

        if (policy == null)
            return new List<Guid>();

        var inheritedTenants = new List<Guid>();

        // Get all descendants of the policy's tenant
        var descendants = await GetTenantDescendantsAsync(policy.TenantId, cancellationToken);

        foreach (var descendantId in descendants)
        {
            var depth = await GetTenantDepthDifferenceAsync(policy.TenantId, descendantId, cancellationToken);

            var inherits = policy.InheritanceScope switch
            {
                InheritanceScope.Children => depth == 1,
                InheritanceScope.Descendants => depth > 0,
                _ => false
            };

            if (inherits)
                inheritedTenants.Add(descendantId);
        }

        return inheritedTenants;
    }

    public async Task<PolicyEvaluationResult> EvaluateAsync(
        Guid userId,
        Guid tenantId,
        string resource,
        string action,
        PolicyEvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        context ??= new PolicyEvaluationContext();

        _logger.LogDebug(
            "Evaluating access: User={UserId}, Tenant={TenantId}, Resource={Resource}, Action={Action}",
            userId, tenantId, resource, action);

        // Get user's roles in this tenant (including parent tenants)
        var userRoles = await GetUserRolesInHierarchyAsync(userId, tenantId, cancellationToken);

        // Get effective policies for this tenant
        var effectivePolicies = await GetEffectivePoliciesForTenantAsync(tenantId, cancellationToken);

        // Filter policies that match this user, resource, and action
        var matchingPolicies = effectivePolicies.Where(p =>
            MatchesUser(p, userId, userRoles) &&
            MatchesResource(p, resource) &&
            MatchesAction(p, action) &&
            MatchesTimeConstraints(p, context) &&
            MatchesConditions(p, context)
        ).ToList();

        stopwatch.Stop();

        // Evaluation logic: Explicit deny wins, then explicit allow, then default deny
        var denyPolicy = matchingPolicies.FirstOrDefault(p => p.Effect == PolicyEffect.Deny);
        var allowPolicy = matchingPolicies.FirstOrDefault(p => p.Effect == PolicyEffect.Allow);

        PolicyEvaluationResult result;
        if (denyPolicy != null)
        {
            result = new PolicyEvaluationResult
            {
                IsAllowed = false,
                MatchedPolicy = denyPolicy,
                PolicyId = denyPolicy.Id,
                Reason = $"Explicitly denied by policy '{denyPolicy.Name}' (Priority: {denyPolicy.Priority})",
                EvaluatedPolicies = matchingPolicies,
                EvaluatedPoliciesCount = matchingPolicies.Count,
                EvaluationTimeMs = stopwatch.ElapsedMilliseconds
            };
        }
        else if (allowPolicy != null)
        {
            result = new PolicyEvaluationResult
            {
                IsAllowed = true,
                MatchedPolicy = allowPolicy,
                PolicyId = allowPolicy.Id,
                Reason = $"Allowed by policy '{allowPolicy.Name}' (Priority: {allowPolicy.Priority})",
                EvaluatedPolicies = matchingPolicies,
                EvaluatedPoliciesCount = matchingPolicies.Count,
                EvaluationTimeMs = stopwatch.ElapsedMilliseconds
            };
        }
        else
        {
            // Default deny
            result = new PolicyEvaluationResult
            {
                IsAllowed = false,
                MatchedPolicy = null,
                PolicyId = null,
                Reason = "No matching policy found (default deny)",
                EvaluatedPolicies = matchingPolicies,
                EvaluatedPoliciesCount = matchingPolicies.Count,
                EvaluationTimeMs = stopwatch.ElapsedMilliseconds
            };
        }

        PublishPolicyEvaluatedEvent(userId, tenantId, resource, action, result);

        return result;
    }

    public async Task<PolicyImpactAnalysis> SimulatePolicyImpactAsync(
        Policy policy,
        CancellationToken cancellationToken = default)
    {
        var affectedTenantIds = new List<Guid> { policy.TenantId };

        // Add descendants based on inheritance scope
        if (policy.InheritanceScope != InheritanceScope.Self)
        {
            var descendants = await GetTenantDescendantsAsync(policy.TenantId, cancellationToken);
            affectedTenantIds.AddRange(descendants);
        }

        // Get all users who have roles in affected tenants
        var affectedUserIds = await _context.UserRoles
            .Where(ur => ur.TenantId.HasValue && affectedTenantIds.Contains(ur.TenantId.Value))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return new PolicyImpactAnalysis
        {
            AffectedTenantCount = affectedTenantIds.Count,
            AffectedTenantIds = affectedTenantIds,
            AffectedUserCount = affectedUserIds.Count,
            AffectedUserIds = affectedUserIds,
            Summary = $"Policy affects {affectedUserIds.Count} users across {affectedTenantIds.Count} tenants"
        };
    }

    #region Private Helper Methods

    /// <summary>
    /// Gets the hierarchy path from root to target tenant (includes target).
    /// Returns list ordered from root to leaf.
    /// </summary>
    private async Task<List<Guid>> GetTenantHierarchyPathAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var path = new List<Guid>();
        var currentId = tenantId;

        while (currentId != Guid.Empty)
        {
            path.Insert(0, currentId); // Prepend to maintain root-to-leaf order

            var tenant = await _context.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == currentId, cancellationToken);

            if (tenant?.ParentTenantId == null)
                break;

            currentId = tenant.ParentTenantId.Value;
        }

        return path;
    }

    /// <summary>
    /// Gets all descendant tenant IDs (recursive)
    /// </summary>
    private async Task<List<Guid>> GetTenantDescendantsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var descendants = new List<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(tenantId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();

            var children = await _context.Tenants
                .AsNoTracking()
                .Where(t => t.ParentTenantId == currentId)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            foreach (var childId in children)
            {
                descendants.Add(childId);
                queue.Enqueue(childId);
            }
        }

        return descendants;
    }

    /// <summary>
    /// Calculates depth difference between ancestor and descendant
    /// </summary>
    private async Task<int> GetTenantDepthDifferenceAsync(
        Guid ancestorId,
        Guid descendantId,
        CancellationToken cancellationToken)
    {
        var descendantPath = await GetTenantHierarchyPathAsync(descendantId, cancellationToken);
        var ancestorIndex = descendantPath.IndexOf(ancestorId);
        var descendantIndex = descendantPath.IndexOf(descendantId);

        if (ancestorIndex == -1 || descendantIndex == -1)
            return -1; // Not in same hierarchy

        return descendantIndex - ancestorIndex;
    }

    /// <summary>
    /// Gets user's roles in target tenant and all parent tenants
    /// </summary>
    private async Task<List<Guid>> GetUserRolesInHierarchyAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var hierarchyPath = await GetTenantHierarchyPathAsync(tenantId, cancellationToken);

        var roleIds = await _context.UserRoles
            .Where(ur => ur.UserId == userId && ur.TenantId.HasValue && hierarchyPath.Contains(ur.TenantId.Value))
            .Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken);

        return roleIds;
    }

    private bool MatchesUser(Policy policy, Guid userId, List<Guid> userRoleIds)
    {
        // Policy applies to specific user
        if (policy.UserId.HasValue)
            return policy.UserId.Value == userId;

        // Policy applies to specific role
        if (policy.RoleId.HasValue)
            return userRoleIds.Contains(policy.RoleId.Value);

        // Policy applies to all users
        return true;
    }

    private bool MatchesResource(Policy policy, string resource)
    {
        // Exact match
        if (policy.Resource.Equals(resource, StringComparison.OrdinalIgnoreCase))
            return true;

        // Wildcard match (e.g., "Door:*" matches "Door:Unlock")
        if (policy.Resource.EndsWith("*"))
        {
            var prefix = policy.Resource[..^1];
            return resource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool MatchesAction(Policy policy, string action)
    {
        return policy.Action.Equals(action, StringComparison.OrdinalIgnoreCase) ||
               policy.Action.Equals("*", StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesTimeConstraints(Policy policy, PolicyEvaluationContext context)
    {
        if (string.IsNullOrWhiteSpace(policy.TimeConstraints))
            return true;

        var constraints = policy.GetTimeConstraints();
        if (constraints == null)
            return true;

        var now = context.EvaluationTime;

        // Check date range
        if (constraints.StartDate.HasValue && now < constraints.StartDate.Value)
            return false;

        if (constraints.EndDate.HasValue && now > constraints.EndDate.Value)
            return false;

        // Check day of week
        if (constraints.DaysOfWeek.Count > 0)
        {
            var dayOfWeek = (int)now.DayOfWeek;
            if (dayOfWeek == 0) dayOfWeek = 7; // Sunday = 7
            if (!constraints.DaysOfWeek.Contains(dayOfWeek))
                return false;
        }

        // Check time of day (simplified - assumes UTC for now)
        if (!string.IsNullOrWhiteSpace(constraints.StartTime) && !string.IsNullOrWhiteSpace(constraints.EndTime))
        {
            var timeOfDay = now.TimeOfDay;
            var startTime = TimeSpan.Parse(constraints.StartTime);
            var endTime = TimeSpan.Parse(constraints.EndTime);

            if (timeOfDay < startTime || timeOfDay > endTime)
                return false;
        }

        return true;
    }

    private bool MatchesConditions(Policy policy, PolicyEvaluationContext context)
    {
        if (string.IsNullOrWhiteSpace(policy.Conditions))
            return true;

        var conditions = policy.GetConditions();
        if (conditions == null || conditions.Count == 0)
            return true;

        // Simplified condition matching (can be extended)
        foreach (var (key, value) in conditions)
        {
            switch (key.ToLowerInvariant())
            {
                case "device_health":
                    if (context.DeviceHealth != value.ToString())
                        return false;
                    break;

                case "ip_whitelist":
                    // TODO: Implement IP range checking
                    break;

                case "location":
                    if (context.Location != value.ToString())
                        return false;
                    break;
            }
        }

        return true;
    }

    #endregion
}
