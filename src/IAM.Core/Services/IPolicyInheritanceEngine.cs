using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for evaluating policies with spatial inheritance support.
/// Handles hierarchical policy traversal through tenant tree.
/// </summary>
public interface IPolicyInheritanceEngine
{
    /// <summary>
    /// Gets all effective policies for a given tenant (including inherited from parents).
    /// Traverses up the hierarchy and collects all applicable policies.
    /// </summary>
    /// <param name="tenantId">Target tenant ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of effective policies sorted by priority (highest first)</returns>
    Task<List<Policy>> GetEffectivePoliciesForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all policies inherited by descendants of the given tenant.
    /// Traverses down the hierarchy based on InheritanceScope.
    /// </summary>
    /// <param name="policyId">Source policy ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of tenants that inherit this policy</returns>
    Task<List<Guid>> GetInheritedByTenantsAsync(Guid policyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether a specific action is allowed for a user on a resource within a tenant context.
    /// </summary>
    /// <param name="userId">User requesting access</param>
    /// <param name="tenantId">Tenant context (spatial scope)</param>
    /// <param name="resource">Resource being accessed</param>
    /// <param name="action">Action being performed</param>
    /// <param name="context">Additional context (IP, device, time, etc.)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Authorization result with detailed reasoning</returns>
    Task<PolicyEvaluationResult> EvaluateAsync(
        Guid userId,
        Guid tenantId,
        string resource,
        string action,
        PolicyEvaluationContext? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Simulates policy impact analysis (what would change if this policy were applied).
    /// Used for policy testing before deployment.
    /// </summary>
    /// <param name="policy">Policy to simulate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Impact analysis showing affected users and tenants</returns>
    Task<PolicyImpactAnalysis> SimulatePolicyImpactAsync(Policy policy, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a policy evaluation
/// </summary>
public class PolicyEvaluationResult
{
    /// <summary>
    /// Whether access is granted
    /// </summary>
    public bool IsAllowed { get; set; }

    /// <summary>
    /// Policy that made the decision (null if default deny)
    /// </summary>
    public Policy? MatchedPolicy { get; set; }

    /// <summary>
    /// Reason for the decision (for audit logs and debugging)
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// All policies that were evaluated (for transparency)
    /// </summary>
    public List<Policy> EvaluatedPolicies { get; set; } = new();

    /// <summary>
    /// Evaluation duration in milliseconds
    /// </summary>
    public long EvaluationTimeMs { get; set; }
}

/// <summary>
/// Context for policy evaluation (environmental factors)
/// </summary>
public class PolicyEvaluationContext
{
    public string? IpAddress { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceHealth { get; set; }
    public string? Location { get; set; }
    public DateTime EvaluationTime { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> CustomAttributes { get; set; } = new();
}

/// <summary>
/// Impact analysis for a policy
/// </summary>
public class PolicyImpactAnalysis
{
    public int AffectedTenantCount { get; set; }
    public List<Guid> AffectedTenantIds { get; set; } = new();
    public int AffectedUserCount { get; set; }
    public List<Guid> AffectedUserIds { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
}
