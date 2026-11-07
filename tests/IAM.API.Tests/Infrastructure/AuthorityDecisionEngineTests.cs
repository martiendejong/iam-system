using IAM.Core.Authority;
using IAM.Core.Entities;
using Xunit;

namespace IAM.API.Tests.Infrastructure;

/// <summary>Task 4844: Authority Resolver decision logic (pure engine, no database).</summary>
public class AuthorityDecisionEngineTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid PgTenant = Guid.NewGuid();
    private static readonly Guid PerridonTenant = Guid.NewGuid();

    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid PgLead = Guid.NewGuid();
    private static readonly Guid PgDirector = Guid.NewGuid();
    private static readonly Guid PerridonOwner = Guid.NewGuid();
    private static readonly Guid PerridonBoardA = Guid.NewGuid();
    private static readonly Guid PerridonBoardB = Guid.NewGuid();

    private static CommandStage Stage(int order, int required, int? timeout, params CommandStageMember[] members)
    {
        var s = new CommandStage { Order = order, Name = "Stage " + order, RequiredApprovals = required, EscalationTimeoutSeconds = timeout };
        foreach (var m in members) { m.CommandStageId = s.Id; s.Members.Add(m); }
        return s;
    }

    private static CommandStageMember U(Guid id) => new() { Kind = CommandStageMemberKind.User, UserId = id };

    private static CommandChain Chain(params CommandStage[] stages)
    {
        var c = new CommandChain { Name = "chain", EffectiveFrom = Now.AddYears(-1) };
        foreach (var s in stages) { s.CommandChainId = c.Id; s.CommandChain = c; c.Stages.Add(s); }
        return c;
    }

    private static AuthorityFacts Facts(Guid tenant, CommandChain? chain, Guid? actor = null,
        RbacOutcome rbac = RbacOutcome.Allowed, Action<AuthorityRequestContext>? ctx = null,
        IReadOnlyDictionary<Guid, bool>? availability = null, IReadOnlyList<AuthorityDelegation>? delegations = null,
        IReadOnlyDictionary<Guid, string>? sod = null, string? continuity = null, bool domainEnabled = true,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? memberUsers = null)
    {
        var domain = new AuthorityDomain { TenantId = tenant, Key = "deploys", Name = "Deploys", IsEnabled = domainEnabled, Version = 3 };
        foreach (var d in delegations ?? Array.Empty<AuthorityDelegation>()) d.AuthorityDomainId = domain.Id;
        var policy = chain is null ? null : new AuthorityPolicy
        {
            TenantId = tenant, AuthorityDomainId = domain.Id, CommandChainId = chain.Id, CommandChain = chain,
            ContinuityPolicyRef = continuity, Version = 7
        };
        var context = new AuthorityRequestContext();
        ctx?.Invoke(context);
        return new AuthorityFacts
        {
            Request = new AuthorityRequest { TenantId = tenant, DomainKey = "deploys", ActorUserId = actor ?? Requester, Context = context },
            NowUtc = Now, Domain = domain, Policy = policy, Chain = chain, Rbac = rbac,
            Availability = availability ?? new Dictionary<Guid, bool>(),
            Delegations = delegations ?? Array.Empty<AuthorityDelegation>(),
            SodBlockedApprovers = sod ?? new Dictionary<Guid, string>(),
            MemberUsers = memberUsers ?? new Dictionary<Guid, IReadOnlyList<Guid>>()
        };
    }

    // ProsperGenics: lead decides, director above. Perridon: owner alone, no escalation.
    private static CommandChain ProsperGenicsChain() =>
        Chain(Stage(1, 1, 3600, U(PgLead)), Stage(2, 1, null, U(PgDirector)));

    private static CommandChain PerridonChain() =>
        Chain(Stage(1, 2, null, U(PerridonBoardA), U(PerridonBoardB), U(PerridonOwner)));

    [Fact]
    public void ProsperGenics_requester_asks_lead_for_approval_with_policy_and_version_ids()
    {
        var f = Facts(PgTenant, ProsperGenicsChain(), ctx: c => c.StageEngagedAtUtc = Now.AddMinutes(-10));
        var r = AuthorityDecisionEngine.Resolve(f);

        Assert.Equal(AuthorityDecisionState.RequiresApproval, r.State);
        Assert.Equal(PgLead, Assert.Single(r.Authorities).UserId);
        Assert.Equal(PgDirector, r.EscalationTarget!.Holders.Single().UserId);
        Assert.Equal(Now.AddMinutes(-10).AddHours(1), r.DeadlineUtc);
        Assert.Equal(7, r.Policy!.PolicyVersion);
        Assert.Equal(3, r.Policy.DomainVersion);
        Assert.Contains(r.Reasons, x => x.Code == "POLICY_APPLIED");
        Assert.Contains(r.Reasons, x => x.Code == "APPROVAL_REQUIRED");
    }

    [Fact]
    public void Perridon_needs_two_approvals_from_others_and_requester_owner_cannot_count_alone()
    {
        // The owner asking for a decision cannot approve it alone: 4-eyes chain.
        var r = AuthorityDecisionEngine.Resolve(Facts(PerridonTenant, PerridonChain(), actor: PerridonOwner));
        Assert.Equal(AuthorityDecisionState.RequiresApproval, r.State);
        Assert.Equal(2, r.RequiredApprovals);
        Assert.DoesNotContain(r.Authorities, h => h.UserId == PerridonOwner);
        Assert.Contains(r.Reasons, x => x.Code == "FOUR_EYES_REQUIRED");
        Assert.Null(r.EscalationTarget);
    }

    [Fact]
    public void Same_actor_gets_different_outcomes_in_the_two_chains()
    {
        var pg = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(), actor: PgLead));
        var per = AuthorityDecisionEngine.Resolve(Facts(PerridonTenant, PerridonChain(), actor: PerridonBoardA));
        Assert.Equal(AuthorityDecisionState.Allow, pg.State);
        Assert.Equal(AuthorityDecisionState.RequiresApproval, per.State);
    }

    [Fact]
    public void Allow_needs_the_rbac_permission_too()
    {
        // The lead is the only member of stage 1 and cannot decide alone without the permission, so the director gets it.
        var r = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(), actor: PgLead, rbac: RbacOutcome.NotGranted));
        Assert.Equal(AuthorityDecisionState.Escalate, r.State);
        Assert.Equal(PgDirector, r.Authorities.Single().UserId);
        Assert.Contains(r.Reasons, x => x.Code == "RBAC_NOT_GRANTED_TO_ACTOR");
    }

    [Fact]
    public void Explicit_rbac_deny_is_a_hard_deny_even_for_the_chain_head()
    {
        var r = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(), actor: PgLead, rbac: RbacOutcome.ExplicitDeny));
        Assert.Equal(AuthorityDecisionState.Deny, r.State);
        Assert.Contains(r.Reasons, x => x.Code == "RBAC_EXPLICIT_DENY");
        Assert.Empty(r.Authorities);
    }

    [Fact]
    public void Extra_hard_deny_reasons_deny()
    {
        var f = Facts(PgTenant, ProsperGenicsChain());
        var withDeny = new AuthorityFacts
        {
            Request = f.Request, NowUtc = f.NowUtc, Domain = f.Domain, Policy = f.Policy, Chain = f.Chain,
            HardDenies = new[] { new AuthorityReason("ABAC_GEO_BLOCK", "Outside allowed region.") }
        };
        var r = AuthorityDecisionEngine.Resolve(withDeny);
        Assert.Equal(AuthorityDecisionState.Deny, r.State);
        Assert.Contains(r.Reasons, x => x.Code == "ABAC_GEO_BLOCK");
    }

    [Fact]
    public void No_policy_and_missing_or_disabled_domain_deny_by_default()
    {
        Assert.Contains(AuthorityDecisionEngine.Resolve(Facts(PgTenant, null)).Reasons, x => x.Code == "NO_APPLICABLE_POLICY");
        Assert.Contains(AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(), domainEnabled: false)).Reasons,
            x => x.Code == "DOMAIN_DISABLED");
        var noDomain = new AuthorityFacts { Request = Facts(PgTenant, null).Request, NowUtc = Now };
        var r = AuthorityDecisionEngine.Resolve(noDomain);
        Assert.Equal(AuthorityDecisionState.Deny, r.State);
        Assert.Contains(r.Reasons, x => x.Code == "DOMAIN_NOT_FOUND");
    }

    [Fact]
    public void Timed_out_stage_escalates_to_the_next_stage()
    {
        var r = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(),
            ctx: c => c.StageEngagedAtUtc = Now.AddHours(-2)));
        Assert.Equal(AuthorityDecisionState.Escalate, r.State);
        Assert.Equal(2, r.Stage!.Order);
        Assert.Equal(PgDirector, Assert.Single(r.Authorities).UserId);
        Assert.Contains(r.Reasons, x => x.Code == "STAGE_TIMED_OUT");
    }

    [Fact]
    public void Unavailable_lead_escalates_and_unknown_availability_does_not()
    {
        var down = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(),
            availability: new Dictionary<Guid, bool> { [PgLead] = false }));
        Assert.Equal(AuthorityDecisionState.Escalate, down.State);
        Assert.Equal(PgDirector, down.Authorities.Single().UserId);

        var unknown = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain()));
        Assert.Equal(AuthorityDecisionState.RequiresApproval, unknown.State);
    }

    [Fact]
    public void Sod_conflict_excludes_an_approver_and_pushes_to_next_stage()
    {
        var r = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(),
            sod: new Dictionary<Guid, string> { [PgLead] = "Requester vs Lead" }));
        Assert.Equal(AuthorityDecisionState.Escalate, r.State);
        Assert.Equal(PgDirector, r.Authorities.Single().UserId);
        Assert.Contains(r.Reasons, x => x.Code == "SOD_APPROVER_EXCLUDED");
    }

    [Fact]
    public void Active_delegation_makes_the_delegate_an_authority_and_expired_does_not()
    {
        var delegate_ = Guid.NewGuid();
        AuthorityDelegation Del(DateTime? until, AuthorityDelegationStatus st = AuthorityDelegationStatus.Active) => new()
        {
            DelegatorUserId = PgLead, DelegateUserId = delegate_, EffectiveFrom = Now.AddDays(-1), EffectiveUntil = until, Status = st
        };

        var f = Facts(PgTenant, ProsperGenicsChain(), delegations: new[] { Del(Now.AddDays(1)) });
        var r = AuthorityDecisionEngine.Resolve(f);
        Assert.Contains(r.Authorities, h => h.UserId == delegate_ && h.Via == "delegation");

        var expired = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(), delegations: new[] { Del(Now.AddHours(-1)) }));
        Assert.DoesNotContain(expired.Authorities, h => h.UserId == delegate_);
        var revoked = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(),
            delegations: new[] { Del(null, AuthorityDelegationStatus.Revoked) }));
        Assert.DoesNotContain(revoked.Authorities, h => h.UserId == delegate_);

        // A delegate acting as requester may decide (ALLOW) because the delegation grants stage authority.
        var asDelegate = AuthorityDecisionEngine.Resolve(Facts(PgTenant, ProsperGenicsChain(), actor: delegate_,
            delegations: new[] { Del(Now.AddDays(1)) }));
        Assert.Equal(AuthorityDecisionState.Allow, asDelegate.State);
    }

    [Fact]
    public void Group_member_expansion_yields_individual_holders()
    {
        var g1 = Guid.NewGuid(); var g2 = Guid.NewGuid();
        var member = new CommandStageMember { Kind = CommandStageMemberKind.Group, GroupId = Guid.NewGuid() };
        var chain = Chain(Stage(1, 1, null, member));
        var r = AuthorityDecisionEngine.Resolve(Facts(PgTenant, chain, memberUsers:
            new Dictionary<Guid, IReadOnlyList<Guid>> { [member.Id] = new[] { g1, g2 } }));
        Assert.Equal(2, r.Authorities.Count);
        Assert.All(r.Authorities, h => Assert.Equal("group", h.Via));
    }

    [Fact]
    public void Exhausted_chain_with_critical_evidenced_reversible_request_is_continuity_authorized()
    {
        var chain = Chain(Stage(1, 1, null, U(PgLead)));
        var r = AuthorityDecisionEngine.Resolve(Facts(PgTenant, chain, continuity: "continuity/pg-1",
            availability: new Dictionary<Guid, bool> { [PgLead] = false },
            ctx: c => { c.Severity = AuthorityLevel.Critical; c.Evidence.Add("alert-991"); c.Reversible = true; }));
        Assert.Equal(AuthorityDecisionState.ContinuityAuthorized, r.State);
        Assert.Equal("continuity/pg-1", r.ContinuityPolicyRef);
    }

    [Theory]
    [InlineData(false, true, true, "CONTINUITY_NOT_CONFIGURED")]
    [InlineData(true, false, true, "CONTINUITY_EVIDENCE_MISSING")]
    [InlineData(true, true, false, "CONTINUITY_IRREVERSIBLE_REFUSED")]
    public void Continuity_is_refused_without_policy_evidence_or_when_irreversible(
        bool hasPolicy, bool hasEvidence, bool reversible, string code)
    {
        var chain = Chain(Stage(1, 1, null, U(PgLead)));
        var r = AuthorityDecisionEngine.Resolve(Facts(PgTenant, chain, continuity: hasPolicy ? "c/1" : null,
            availability: new Dictionary<Guid, bool> { [PgLead] = false },
            ctx: c => { c.Urgency = AuthorityLevel.Critical; c.Reversible = reversible; if (hasEvidence) c.Evidence.Add("e1"); }));
        Assert.Equal(AuthorityDecisionState.Escalate, r.State);
        Assert.Contains(r.Reasons, x => x.Code == "CHAIN_EXHAUSTED");
        Assert.Contains(r.Reasons, x => x.Code == code);
    }

    [Fact]
    public void Continuity_needs_high_urgency_or_severity()
    {
        var chain = Chain(Stage(1, 1, null, U(PgLead)));
        var r = AuthorityDecisionEngine.Resolve(Facts(PgTenant, chain, continuity: "c/1",
            availability: new Dictionary<Guid, bool> { [PgLead] = false },
            ctx: c => { c.Urgency = AuthorityLevel.Medium; c.Evidence.Add("e1"); }));
        Assert.Equal(AuthorityDecisionState.Escalate, r.State);
        Assert.Contains(r.Reasons, x => x.Code == "CONTINUITY_URGENCY_TOO_LOW");
    }

    [Fact]
    public void Scoped_override_picks_a_different_chain_than_the_org_default()
    {
        // Policy selection is AuthorityPolicyResolver (task 4843); here we assert the engine reports which one applied.
        var orgChain = ProsperGenicsChain();
        var overrideChain = Chain(Stage(1, 1, null, U(PgDirector)));
        var org = new AuthorityPolicy { TenantId = PgTenant, CommandChainId = orgChain.Id, CommandChain = orgChain, ScopeLevel = AuthorityScopeLevel.Organization, EffectiveFrom = Now.AddDays(-30) };
        var ovr = new AuthorityPolicy
        {
            TenantId = PgTenant, CommandChainId = overrideChain.Id, CommandChain = overrideChain,
            ScopeLevel = AuthorityScopeLevel.Project, ProjectKey = "prod", IsExplicitOverride = true, EffectiveFrom = Now.AddDays(-30)
        };
        var domainId = Guid.NewGuid();
        org.AuthorityDomainId = ovr.AuthorityDomainId = domainId;

        var picked = AuthorityPolicyResolver.Resolve(new[] { org, ovr }, PgTenant, domainId, "prod", null, null, null, Now);
        Assert.Same(ovr, picked);

        var f = Facts(PgTenant, picked!.CommandChain);
        var r = AuthorityDecisionEngine.Resolve(f);
        Assert.Equal(PgDirector, r.Authorities.Single().UserId);

        var other = AuthorityPolicyResolver.Resolve(new[] { org, ovr }, PgTenant, domainId, "dev", null, null, null, Now);
        Assert.Same(org, other);
    }

    [Fact]
    public void Disabled_chain_denies()
    {
        var chain = ProsperGenicsChain();
        chain.IsEnabled = false;
        var r = AuthorityDecisionEngine.Resolve(Facts(PgTenant, chain));
        Assert.Equal(AuthorityDecisionState.Deny, r.State);
        Assert.Contains(r.Reasons, x => x.Code == "CHAIN_NOT_EFFECTIVE");
    }
}
