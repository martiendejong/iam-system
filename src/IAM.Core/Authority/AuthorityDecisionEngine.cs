using IAM.Core.Entities;

namespace IAM.Core.Authority;

/// <summary>Outcome of the RBAC/ABAC check of the actor on the requested resource and action.</summary>
public enum RbacOutcome
{
    /// <summary>The actor holds the permission.</summary>
    Allowed,
    /// <summary>No permission found (default deny). The actor can still ask others to decide.</summary>
    NotGranted,
    /// <summary>An explicit Deny rule matched. A hard deny: no chain can override it.</summary>
    ExplicitDeny
}

/// <summary>Everything the engine needs, already loaded. No field is fetched lazily.</summary>
public sealed class AuthorityFacts
{
    public required AuthorityRequest Request { get; init; }
    public required DateTime NowUtc { get; init; }
    public AuthorityDomain? Domain { get; init; }
    public AuthorityPolicy? Policy { get; init; }
    /// <summary>The policy's chain with Stages and their Members loaded.</summary>
    public CommandChain? Chain { get; init; }

    /// <summary>Member id to the user ids it expands to (group members, role holders). Absent for non-user kinds.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> MemberUsers { get; init; } =
        new Dictionary<Guid, IReadOnlyList<Guid>>();

    /// <summary>Active, in-window authority delegations for the domain.</summary>
    public IReadOnlyList<AuthorityDelegation> Delegations { get; init; } = Array.Empty<AuthorityDelegation>();

    /// <summary>Latest known availability per user (false = known unavailable). Missing = unknown.</summary>
    public IReadOnlyDictionary<Guid, bool> Availability { get; init; } = new Dictionary<Guid, bool>();

    public RbacOutcome Rbac { get; init; } = RbacOutcome.NotGranted;
    public string? RbacDetail { get; init; }

    /// <summary>Extra hard-deny reasons from ABAC or other constraints. Any entry denies.</summary>
    public IReadOnlyList<AuthorityReason> HardDenies { get; init; } = Array.Empty<AuthorityReason>();

    /// <summary>Users who may not approve this request because of a blocking SoD constraint (user id to constraint name).</summary>
    public IReadOnlyDictionary<Guid, string> SodBlockedApprovers { get; init; } = new Dictionary<Guid, string>();
}

/// <summary>
/// Pure decision logic of the Authority Resolver (task 4844). Order of evaluation:
/// hard denies, policy and chain validity, stage walk (timeout, staffing, delegation, availability, SoD),
/// then the state: ALLOW, REQUIRES_APPROVAL, ESCALATE, or CONTINUITY_AUTHORIZED when the chain is exhausted.
/// </summary>
public static class AuthorityDecisionEngine
{
    public const string ResolverVersion = "authority-resolver/1";

    public static AuthorityResolution Resolve(AuthorityFacts f)
    {
        var res = new AuthorityResolution { EvaluatedAtUtc = f.NowUtc };
        var ctx = f.Request.Context;

        if (f.Domain is null)
            return Deny(res, "DOMAIN_NOT_FOUND", $"No authority domain '{f.Request.DomainKey}' in this tenant.");
        if (!f.Domain.IsEnabled)
            return Deny(res, "DOMAIN_DISABLED", "The authority domain is disabled.");

        if (f.Policy is null && f.HardDenies.Count > 0)
        {
            res.Reasons.AddRange(f.HardDenies);
            res.State = AuthorityDecisionState.Deny;
            return res;
        }

        if (f.Policy is null || f.Chain is null)
            return Deny(res, "NO_APPLICABLE_POLICY",
                "No enabled authority policy covers this scope at this time (default deny).");

        res.Policy = new AppliedPolicyInfo(f.Policy.Id, f.Policy.Version, f.Chain.Id, f.Chain.Version,
            f.Domain.Id, f.Domain.Version, f.Policy.ScopeLevel, f.Policy.IsExplicitOverride);
        res.ContinuityPolicyRef = f.Policy.ContinuityPolicyRef;
        res.Reasons.Add(new("POLICY_APPLIED",
            $"Policy {f.Policy.Id} v{f.Policy.Version} at {f.Policy.ScopeLevel} scope" +
            (f.Policy.IsExplicitOverride ? " (explicit override)" : "") + "."));

        // Hard denies come first and cannot be overridden by any chain.
        if (f.Rbac == RbacOutcome.ExplicitDeny)
            res.Reasons.Add(new("RBAC_EXPLICIT_DENY", f.RbacDetail ?? "An explicit deny rule matches the actor."));
        foreach (var h in f.HardDenies) res.Reasons.Add(h);
        if (f.Rbac == RbacOutcome.ExplicitDeny || f.HardDenies.Count > 0)
        {
            res.State = AuthorityDecisionState.Deny;
            return res;
        }

        if (!f.Chain.IsEnabled || f.NowUtc < f.Chain.EffectiveFrom ||
            (f.Chain.EffectiveUntil is not null && f.NowUtc >= f.Chain.EffectiveUntil))
        {
            res.Reasons.Add(new("CHAIN_NOT_EFFECTIVE", "The policy's command chain is disabled or outside its effective window."));
            res.State = AuthorityDecisionState.Deny;
            return res;
        }

        var stages = f.Chain.Stages.OrderBy(s => s.Order).ToList();
        if (stages.Count == 0)
        {
            res.Reasons.Add(new("CHAIN_HAS_NO_STAGES", "The command chain has no stages."));
            res.State = AuthorityDecisionState.Deny;
            return res;
        }

        var actor = f.Request.ActorUserId;
        var startOrder = ctx.CurrentStageOrder ?? stages[0].Order;
        var idx = stages.FindIndex(s => s.Order == startOrder);
        if (idx < 0)
        {
            res.Reasons.Add(new("INVALID_CURRENT_STAGE", $"The chain has no stage {startOrder}."));
            res.State = AuthorityDecisionState.Deny;
            return res;
        }

        var engagedAt = ctx.StageEngagedAtUtc;
        var moved = false;
        var allUnavailableByCaller = ctx.HumanAvailability == HumanAvailability.Unavailable;

        for (; idx < stages.Count; idx++)
        {
            var stage = stages[idx];

            // Timeout: the stage lost its turn; the next stage's clock starts when this one expired.
            if (stage.EscalationTimeoutSeconds is int secs && engagedAt is not null)
            {
                var expiresAt = engagedAt.Value.AddSeconds(secs);
                if (f.NowUtc >= expiresAt && idx + 1 < stages.Count)
                {
                    res.Reasons.Add(new("STAGE_TIMED_OUT",
                        $"Stage {stage.Order} '{stage.Name}' passed its {secs}s timeout without a decision."));
                    engagedAt = expiresAt;
                    moved = true;
                    continue;
                }
            }

            var holders = BuildHolders(f, stage);
            var eligible = new List<AuthorityHolder>();
            foreach (var h in holders)
            {
                if (h.UserId is Guid uid)
                {
                    if (uid != actor && f.SodBlockedApprovers.TryGetValue(uid, out var sod))
                    {
                        res.Reasons.Add(new("SOD_APPROVER_EXCLUDED", $"User {uid} excluded by SoD constraint '{sod}'."));
                        continue;
                    }
                    if (uid != actor && (allUnavailableByCaller || (f.Availability.TryGetValue(uid, out var avail) && !avail)))
                    {
                        res.Reasons.Add(new("HOLDER_UNAVAILABLE", $"User {uid} is unavailable."));
                        continue;
                    }
                }
                eligible.Add(h);
            }

            var actorHolds = eligible.Any(h => h.UserId == actor);
            var others = eligible.Where(h => h.UserId != actor).ToList();
            var selfDecides = actorHolds && f.Rbac == RbacOutcome.Allowed && stage.RequiredApprovals <= 1;
            if (actorHolds && f.Rbac != RbacOutcome.Allowed)
                res.Reasons.Add(new("RBAC_NOT_GRANTED_TO_ACTOR",
                    f.RbacDetail ?? "The actor lacks the permission and cannot decide alone."));
            if (actorHolds && !selfDecides && stage.RequiredApprovals > 1)
                res.Reasons.Add(new("FOUR_EYES_REQUIRED",
                    $"Stage {stage.Order} needs {stage.RequiredApprovals} approvals; the requester cannot count toward them."));

            var approverCount = DistinctApprovers(others);
            var staffed = selfDecides || approverCount >= Math.Max(1, stage.RequiredApprovals);
            if (!staffed)
            {
                res.Reasons.Add(new("STAGE_UNSTAFFED",
                    $"Stage {stage.Order} '{stage.Name}' has {approverCount} eligible approver(s), needs {Math.Max(1, stage.RequiredApprovals)}."));
                moved = true;
                continue;
            }

            // Stage found.
            res.Stage = ToInfo(stage, eligible);
            res.RequiredApprovals = Math.Max(1, stage.RequiredApprovals);
            res.DeadlineUtc = stage.EscalationTimeoutSeconds is int t && engagedAt is not null
                ? engagedAt.Value.AddSeconds(t) : null;
            res.EscalationTarget = idx + 1 < stages.Count
                ? ToInfo(stages[idx + 1], BuildHolders(f, stages[idx + 1])) : null;

            if (selfDecides)
            {
                res.Authorities = eligible.Where(h => h.UserId == actor).ToList();
                res.RequiredApprovals = 1;
                res.Reasons.Add(new("ACTOR_HOLDS_AUTHORITY",
                    $"The actor holds command authority at stage {stage.Order} and the RBAC permission."));
                res.State = AuthorityDecisionState.Allow;
            }
            else
            {
                res.Authorities = others;
                res.Reasons.Add(new(moved ? "ESCALATED_TO_STAGE" : "APPROVAL_REQUIRED",
                    $"Stage {stage.Order} '{stage.Name}' decides."));
                res.State = moved ? AuthorityDecisionState.Escalate : AuthorityDecisionState.RequiresApproval;
            }
            return res;
        }

        // Every stage from the current one on is out of reach.
        res.Stage = ToInfo(stages[^1], BuildHolders(f, stages[^1]));
        res.Reasons.Add(new("CHAIN_EXHAUSTED", "No remaining stage has enough eligible approvers."));

        var continuity = CheckContinuity(f, res);
        if (continuity)
        {
            res.State = AuthorityDecisionState.ContinuityAuthorized;
            res.Reasons.Add(new("CONTINUITY_AUTHORIZED",
                $"Continuity policy '{f.Policy.ContinuityPolicyRef}' authorizes this action."));
        }
        else
        {
            res.State = AuthorityDecisionState.Escalate;
            res.Authorities = res.Stage.Holders.ToList();
        }
        return res;
    }

    private static bool CheckContinuity(AuthorityFacts f, AuthorityResolution res)
    {
        var ctx = f.Request.Context;
        var ok = true;
        if (string.IsNullOrWhiteSpace(f.Policy!.ContinuityPolicyRef))
        { res.Reasons.Add(new("CONTINUITY_NOT_CONFIGURED", "The policy has no continuity policy.")); ok = false; }
        if ((AuthorityLevel)Math.Max((int)ctx.Urgency, (int)ctx.Severity) < AuthorityLevel.High)
        { res.Reasons.Add(new("CONTINUITY_URGENCY_TOO_LOW", "Continuity needs urgency or severity of High or above.")); ok = false; }
        if (ctx.Evidence.Count(e => !string.IsNullOrWhiteSpace(e)) == 0)
        { res.Reasons.Add(new("CONTINUITY_EVIDENCE_MISSING", "Continuity needs at least one evidence reference.")); ok = false; }
        if (ctx.Reversible == false)
        { res.Reasons.Add(new("CONTINUITY_IRREVERSIBLE_REFUSED", "Irreversible actions are never authorized by continuity.")); ok = false; }
        return ok;
    }

    private static AuthorityResolution Deny(AuthorityResolution res, string code, string message)
    {
        res.State = AuthorityDecisionState.Deny;
        res.Reasons.Add(new(code, message));
        return res;
    }

    private static int DistinctApprovers(IEnumerable<AuthorityHolder> holders) =>
        holders.Select(h => h.UserId?.ToString() ?? h.ServiceAccountId?.ToString() ?? h.ExternalAuthorityRef ?? "?")
            .Distinct().Count();

    private static AuthorityStageInfo ToInfo(CommandStage s, IReadOnlyList<AuthorityHolder> holders) =>
        new(s.Id, s.Order, s.Name, Math.Max(1, s.RequiredApprovals), s.EscalationTimeoutSeconds, holders);

    private static List<AuthorityHolder> BuildHolders(AuthorityFacts f, CommandStage stage)
    {
        var list = new List<AuthorityHolder>();
        foreach (var m in stage.Members)
        {
            switch (m.Kind)
            {
                case CommandStageMemberKind.User:
                    list.Add(new(m.Kind, m.UserId, null, null, null, null, "member"));
                    break;
                case CommandStageMemberKind.Group:
                case CommandStageMemberKind.Role:
                    var via = m.Kind == CommandStageMemberKind.Group ? "group" : "role";
                    if (f.MemberUsers.TryGetValue(m.Id, out var users))
                        foreach (var u in users)
                            list.Add(new(CommandStageMemberKind.User, u, m.GroupId, m.RoleId, null, null, via));
                    break;
                case CommandStageMemberKind.Agent:
                    list.Add(new(m.Kind, null, null, null, m.ServiceAccountId, null, "member"));
                    break;
                case CommandStageMemberKind.ExternalAuthority:
                    list.Add(new(m.Kind, null, null, null, null, m.ExternalAuthorityRef, "member"));
                    break;
            }
        }

        // Delegation: the delegate acts for a delegator who holds this stage, inside the delegation window.
        var holderIds = list.Where(h => h.UserId.HasValue).Select(h => h.UserId!.Value).ToHashSet();
        foreach (var d in f.Delegations)
        {
            if (d.Status != AuthorityDelegationStatus.Active) continue;
            if (f.Domain is not null && d.AuthorityDomainId != f.Domain.Id) continue;
            if (d.CommandStageId is Guid sid && sid != stage.Id) continue;
            if (f.NowUtc < d.EffectiveFrom || (d.EffectiveUntil is not null && f.NowUtc >= d.EffectiveUntil)) continue;
            if (!holderIds.Contains(d.DelegatorUserId)) continue;
            if (list.Any(h => h.UserId == d.DelegateUserId)) continue;
            list.Add(new(CommandStageMemberKind.User, d.DelegateUserId, null, null, null, null, "delegation", d.Id));
        }
        return list;
    }
}
