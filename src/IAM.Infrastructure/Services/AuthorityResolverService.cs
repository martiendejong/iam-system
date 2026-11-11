using IAM.Core.Authority;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

/// <summary>Loads the facts for an authority request and hands them to <see cref="AuthorityDecisionEngine"/>.</summary>
public class AuthorityResolverService : IAuthorityResolverService
{
    private readonly IAMDbContext _db;
    private readonly IUnifiedAuthorizationService _authz;

    public AuthorityResolverService(IAMDbContext db, IUnifiedAuthorizationService authz)
    {
        _db = db;
        _authz = authz;
    }

    public async Task<AuthorityResolution> ResolveAsync(AuthorityRequest req, CancellationToken ct = default)
    {
        var now = req.AtUtc ?? DateTime.UtcNow;
        var key = req.DomainKey?.Trim() ?? string.Empty;

        var domain = await _db.AuthorityDomains
            .FirstOrDefaultAsync(d => d.TenantId == req.TenantId && d.Key == key, ct);

        var hardDenies = new List<AuthorityReason>();
        var actor = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.ActorUserId, ct);
        if (actor is null || !actor.IsActive)
            hardDenies.Add(new("ACTOR_UNKNOWN_OR_INACTIVE", "The requesting user does not exist or is disabled."));

        if (domain is null || hardDenies.Count > 0)
            return AuthorityDecisionEngine.Resolve(new AuthorityFacts
            {
                Request = req, NowUtc = now, Domain = domain, HardDenies = hardDenies
            });

        var policies = await _db.AuthorityPolicies
            .Include(p => p.CommandChain!).ThenInclude(c => c.Stages).ThenInclude(s => s.Members)
            .Where(p => p.TenantId == req.TenantId && p.AuthorityDomainId == domain.Id)
            .ToListAsync(ct);

        var policy = AuthorityPolicyResolver.Resolve(policies, req.TenantId, domain.Id,
            req.ProjectKey, req.ResourceType, req.ResourceId, req.Action, now);
        var chain = policy?.CommandChain;

        var memberUsers = new Dictionary<Guid, IReadOnlyList<Guid>>();
        var availability = new Dictionary<Guid, bool>();
        var sodBlocked = new Dictionary<Guid, string>();
        IReadOnlyList<AuthorityDelegation> delegations = Array.Empty<AuthorityDelegation>();

        if (chain is not null)
        {
            var members = chain.Stages.SelectMany(s => s.Members).ToList();
            await ExpandMembersAsync(req.TenantId, members, memberUsers, now, ct);

            delegations = await _db.AuthorityDelegations.AsNoTracking()
                .Where(d => d.TenantId == req.TenantId && d.AuthorityDomainId == domain.Id &&
                            d.Status == AuthorityDelegationStatus.Active)
                .ToListAsync(ct);

            var candidateIds = memberUsers.Values.SelectMany(v => v)
                .Concat(delegations.Select(d => d.DelegateUserId))
                .Distinct().ToList();

            // Disabled users are not authorities.
            var activeSet = (await _db.Users.AsNoTracking()
                .Where(u => candidateIds.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct)).ToHashSet();
            foreach (var k in memberUsers.Keys.ToList())
                memberUsers[k] = memberUsers[k].Where(activeSet.Contains).ToList();
            delegations = delegations.Where(d => activeSet.Contains(d.DelegateUserId)).ToList();
            candidateIds = candidateIds.Where(activeSet.Contains).ToList();

            // Latest non-expired availability evidence per user.
            var evidence = await _db.AuthorityAvailabilityEvidence.AsNoTracking()
                .Where(e => e.TenantId == req.TenantId && e.UserId != null && candidateIds.Contains(e.UserId.Value) &&
                            e.ObservedAt <= now && (e.ValidUntil == null || e.ValidUntil > now))
                .ToListAsync(ct);
            foreach (var g in evidence.GroupBy(e => e.UserId!.Value))
                availability[g.Key] = g.OrderByDescending(e => e.ObservedAt).First().IsAvailable;

            await AddSodBlocksAsync(req, candidateIds, sodBlocked, ct);
        }

        // RBAC/ABAC for the actor on the target; a matched Deny rule is a hard deny.
        var resource = string.Join(':', new[] { req.ProjectKey, req.ResourceType, req.ResourceId }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (resource.Length == 0) resource = "authority:" + key;
        var authz = await _authz.EvaluateAsync(new AuthorizationRequest
        {
            PrincipalType = "user", PrincipalId = req.ActorUserId, TenantId = req.TenantId,
            Resource = resource, Action = string.IsNullOrWhiteSpace(req.Action) ? "decide" : req.Action!,
            Context = req.Context.Attributes
        }, ct);
        var rbac = authz.Allowed ? RbacOutcome.Allowed
            : authz.MatchedPolicy is not null ? RbacOutcome.ExplicitDeny : RbacOutcome.NotGranted;

        return AuthorityDecisionEngine.Resolve(new AuthorityFacts
        {
            Request = req, NowUtc = now, Domain = domain, Policy = policy, Chain = chain,
            MemberUsers = memberUsers, Delegations = delegations, Availability = availability,
            Rbac = rbac, RbacDetail = authz.Reason, HardDenies = hardDenies, SodBlockedApprovers = sodBlocked
        });
    }

    private async Task ExpandMembersAsync(Guid tenantId, List<CommandStageMember> members,
        Dictionary<Guid, IReadOnlyList<Guid>> memberUsers, DateTime now, CancellationToken ct)
    {
        foreach (var m in members)
        {
            switch (m.Kind)
            {
                case CommandStageMemberKind.User when m.UserId.HasValue:
                    memberUsers[m.Id] = new[] { m.UserId.Value };
                    break;
                case CommandStageMemberKind.Group when m.GroupId.HasValue:
                    memberUsers[m.Id] = await _db.GroupMemberships.AsNoTracking()
                        .Where(gm => gm.GroupId == m.GroupId && gm.IsActive && (gm.ExpiresAt == null || gm.ExpiresAt > now))
                        .Select(gm => gm.UserId).Distinct().ToListAsync(ct);
                    break;
                case CommandStageMemberKind.Role when m.RoleId.HasValue:
                    memberUsers[m.Id] = await _db.UserRoles.AsNoTracking()
                        .Where(ur => ur.RoleId == m.RoleId && ur.TenantId == tenantId &&
                                     (ur.ExpiresAt == null || ur.ExpiresAt > now))
                        .Select(ur => ur.UserId).Distinct().ToListAsync(ct);
                    break;
            }
        }
    }

    /// <summary>A blocking SoD constraint bars a candidate approver who holds the role conflicting with a role of the requester.</summary>
    private async Task AddSodBlocksAsync(AuthorityRequest req, List<Guid> candidates,
        Dictionary<Guid, string> blocked, CancellationToken ct)
    {
        var constraints = await _db.SodConstraints.AsNoTracking()
            .Where(c => c.TenantId == req.TenantId && c.IsActive && c.Severity == SodSeverity.Block).ToListAsync(ct);
        if (constraints.Count == 0) return;

        var roleRows = await _db.UserRoles.AsNoTracking()
            .Where(ur => ur.TenantId == req.TenantId && (ur.UserId == req.ActorUserId || candidates.Contains(ur.UserId)))
            .Select(ur => new { ur.UserId, ur.RoleId }).ToListAsync(ct);
        var actorRoles = roleRows.Where(r => r.UserId == req.ActorUserId).Select(r => r.RoleId).ToHashSet();

        foreach (var c in constraints)
        {
            foreach (var cand in candidates.Where(x => x != req.ActorUserId))
            {
                var candRoles = roleRows.Where(r => r.UserId == cand).Select(r => r.RoleId).ToHashSet();
                var conflict = (actorRoles.Contains(c.ConflictingRoleA) && candRoles.Contains(c.ConflictingRoleB)) ||
                               (actorRoles.Contains(c.ConflictingRoleB) && candRoles.Contains(c.ConflictingRoleA));
                if (conflict) blocked.TryAdd(cand, c.Name);
            }
        }
    }
}
