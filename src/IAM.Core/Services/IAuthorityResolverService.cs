using IAM.Core.Authority;

namespace IAM.Core.Services;

/// <summary>
/// Canonical Authority Resolver (task 4844): answers "who decides this, and may the actor go ahead?"
/// for a (tenant, domain, project/resource/action, actor, context) tuple. Builds on the chain of
/// command model (task 4843) and combines it with RBAC/ABAC, SoD and hard-deny rules.
/// </summary>
public interface IAuthorityResolverService
{
    Task<AuthorityResolution> ResolveAsync(AuthorityRequest request, CancellationToken ct = default);
}
