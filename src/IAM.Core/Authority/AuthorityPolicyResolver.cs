using IAM.Core.Entities;

namespace IAM.Core.Authority;

/// <summary>
/// Picks the policy that governs a (project, resource, action) at a point in time.
/// Inheritance: organization, project, resource, action. The most specific matching policy wins,
/// but a policy flagged <see cref="AuthorityPolicy.IsExplicitOverride"/> beats non-override policies
/// (among overrides the widest scope wins, because it replaces everything below it).
/// Ties break on newest EffectiveFrom, then highest Version.
/// </summary>
public static class AuthorityPolicyResolver
{
    public static AuthorityPolicy? Resolve(
        IEnumerable<AuthorityPolicy> policies, Guid tenantId, Guid domainId,
        string? projectKey, string? resourceType, string? resourceId, string? action, DateTime atUtc)
    {
        var matches = policies.Where(p =>
            p.TenantId == tenantId && p.AuthorityDomainId == domainId && p.IsEffectiveAt(atUtc) &&
            Matches(p, projectKey, resourceType, resourceId, action)).ToList();
        if (matches.Count == 0) return null;

        var overrides = matches.Where(p => p.IsExplicitOverride).ToList();
        if (overrides.Count > 0)
        {
            var widest = overrides.Min(o => o.ScopeLevel);
            return Pick(overrides.Where(p => p.ScopeLevel == widest));
        }

        var narrowest = matches.Max(o => o.ScopeLevel);
        return Pick(matches.Where(p => p.ScopeLevel == narrowest));
    }

    private static AuthorityPolicy Pick(IEnumerable<AuthorityPolicy> candidates) =>
        candidates.OrderByDescending(p => p.EffectiveFrom).ThenByDescending(p => p.Version).First();

    private static bool Matches(AuthorityPolicy p, string? project, string? resType, string? resId, string? action)
    {
        if (p.ScopeLevel >= AuthorityScopeLevel.Project && !Eq(p.ProjectKey, project)) return false;
        if (p.ScopeLevel >= AuthorityScopeLevel.Resource &&
            (!Eq(p.ResourceType, resType) || !Eq(p.ResourceId, resId))) return false;
        if (p.ScopeLevel >= AuthorityScopeLevel.Action && !Eq(p.Action, action)) return false;
        return true;
    }

    private static bool Eq(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a problem description when the scope columns do not fit the level, else null.</summary>
    public static string? ValidateScope(AuthorityPolicy p)
    {
        static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);
        return p.ScopeLevel switch
        {
            AuthorityScopeLevel.Organization when Has(p.ProjectKey) || Has(p.ResourceType) || Has(p.ResourceId) || Has(p.Action)
                => "Organization scope must not set project, resource or action.",
            AuthorityScopeLevel.Project when !Has(p.ProjectKey) || Has(p.ResourceType) || Has(p.ResourceId) || Has(p.Action)
                => "Project scope needs ProjectKey only.",
            AuthorityScopeLevel.Resource when !Has(p.ProjectKey) || !Has(p.ResourceType) || !Has(p.ResourceId) || Has(p.Action)
                => "Resource scope needs ProjectKey, ResourceType and ResourceId.",
            AuthorityScopeLevel.Action when !Has(p.ProjectKey) || !Has(p.ResourceType) || !Has(p.ResourceId) || !Has(p.Action)
                => "Action scope needs ProjectKey, ResourceType, ResourceId and Action.",
            _ => null
        };
    }
}
