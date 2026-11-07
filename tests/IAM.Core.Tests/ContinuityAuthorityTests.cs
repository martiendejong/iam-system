using IAM.Core.Authority;
using Xunit;

namespace IAM.Core.Tests;

public class ContinuityAuthorityTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), Domain = Guid.NewGuid(), Agent = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static ContinuityScope Scope(string action = "restart") => new("shop", "service", "api", action);

    private static ContinuityEnvelope Env(ContinuityActionKind kind = ContinuityActionKind.OutageRemediation,
        ContinuityUrgency immediate = ContinuityUrgency.Critical, string[]? evidence = null,
        bool reversible = false, TimeSpan? delay = null, ContinuityScope? scope = null) =>
        new(Guid.NewGuid(), Tenant, Domain, Agent, kind, scope ?? Scope(), Now.AddDays(-1), Now.AddDays(1),
            immediate, new HashSet<string>(evidence ?? []), reversible, delay);

    private static ContinuityRequest Req(ContinuityActionKind kind = ContinuityActionKind.OutageRemediation,
        ContinuityUrgency urgency = ContinuityUrgency.Critical, string[]? evidence = null, bool reversible = true,
        AuthorityArtifact target = AuthorityArtifact.None, DateTime? resumed = null, DateTime? requested = null,
        ContinuityScope? scope = null) =>
        new(Tenant, Domain, Agent, kind, scope ?? Scope(), urgency, reversible, new HashSet<string>(evidence ?? []),
            requested ?? Now, Now, resumed, target);

    private static ContinuityDecision Eval(ContinuityRequest r, ContinuityEnvelope[] e, HardHumanOnlyConstraint[]? h = null) =>
        ContinuityAuthorityEvaluator.Evaluate(r, e, h ?? []);

    [Fact]
    public void Outage_is_authorized_immediately_and_humans_are_notified()
    {
        var d = Eval(Req(), [Env()]);
        Assert.Equal(ContinuityOutcome.Authorized, d.Outcome);
        Assert.True(d.NotifyHumans);
    }

    [Fact]
    public void Bugfix_needs_verification_evidence()
    {
        var env = Env(ContinuityActionKind.ProductionBugfix, ContinuityUrgency.High, ["tests", "rollback-plan"]);
        var missing = Eval(Req(ContinuityActionKind.ProductionBugfix, ContinuityUrgency.High, ["tests"]), [env]);
        Assert.Equal(ContinuityOutcome.Pending, missing.Outcome);
        Assert.Equal(["rollback-plan"], missing.MissingEvidence);

        var ok = Eval(Req(ContinuityActionKind.ProductionBugfix, ContinuityUrgency.High, ["tests", "rollback-plan"]), [env]);
        Assert.Equal(ContinuityOutcome.Authorized, ok.Outcome);
    }

    [Fact]
    public void Non_urgent_feature_release_stays_pending()
    {
        var env = Env(ContinuityActionKind.FeatureRelease, ContinuityUrgency.Critical);
        var d = Eval(Req(ContinuityActionKind.FeatureRelease, ContinuityUrgency.Normal), [env]);
        Assert.Equal(ContinuityOutcome.Pending, d.Outcome);
    }

    [Fact]
    public void Hard_human_only_constraint_beats_critical_urgency_and_envelope()
    {
        var hard = new HardHumanOnlyConstraint(Tenant, "finance", new ContinuityScope("shop", null, null, null));
        var d = Eval(Req(), [Env()], [hard]);
        Assert.Equal(ContinuityOutcome.Denied, d.Outcome);
        Assert.False(d.NotifyHumans);
    }

    [Fact]
    public void Disabled_hard_constraint_and_other_tenant_constraint_do_not_apply()
    {
        var off = new HardHumanOnlyConstraint(Tenant, "x", Scope(), IsEnabled: false);
        var other = new HardHumanOnlyConstraint(Guid.NewGuid(), "y", Scope());
        Assert.Equal(ContinuityOutcome.Authorized, Eval(Req(), [Env()], [off, other]).Outcome);
    }

    [Theory]
    [InlineData(AuthorityArtifact.Policy)]
    [InlineData(AuthorityArtifact.CommandChain)]
    [InlineData(AuthorityArtifact.EvidenceRequirements)]
    [InlineData(AuthorityArtifact.HardBoundary)]
    [InlineData(AuthorityArtifact.ContinuityEnvelope)]
    public void Agent_cannot_modify_the_artifacts_that_grant_its_authority(AuthorityArtifact target)
    {
        var d = Eval(Req(target: target), [Env()]);
        Assert.Equal(ContinuityOutcome.Denied, d.Outcome);
    }

    [Fact]
    public void Authority_is_scoped_to_the_concrete_action_agent_and_kind()
    {
        Assert.Equal(ContinuityOutcome.Pending, Eval(Req(scope: Scope("delete-db")), [Env()]).Outcome);
        Assert.Equal(ContinuityOutcome.Pending, Eval(Req(ContinuityActionKind.Other), [Env()]).Outcome);
        var otherAgent = Req() with { AgentId = Guid.NewGuid() };
        Assert.Equal(ContinuityOutcome.Pending, Eval(otherAgent, [Env()]).Outcome);
    }

    [Fact]
    public void Blanket_or_non_expiring_envelope_is_invalid_and_ignored()
    {
        var blanket = Env(scope: new ContinuityScope(null, null, null, null));
        Assert.NotNull(blanket.Validate());
        Assert.Equal(ContinuityOutcome.Pending, Eval(Req(), [blanket]).Outcome);
        var forever = Env() with { EffectiveUntil = null };
        Assert.NotNull(forever.Validate());
        Assert.Equal(ContinuityOutcome.Pending, Eval(Req(), [forever]).Outcome);
    }

    [Fact]
    public void Expired_or_disabled_envelope_does_not_apply()
    {
        Assert.Equal(ContinuityOutcome.Pending, Eval(Req(), [Env() with { EffectiveUntil = Now.AddMinutes(-1) }]).Outcome);
        Assert.Equal(ContinuityOutcome.Pending, Eval(Req(), [Env() with { IsEnabled = false }]).Outcome);
    }

    [Fact]
    public void Reversibility_requirement_is_enforced()
    {
        var env = Env(reversible: true);
        Assert.Equal(ContinuityOutcome.Pending, Eval(Req(reversible: false), [env]).Outcome);
        Assert.Equal(ContinuityOutcome.Authorized, Eval(Req(reversible: true), [env]).Outcome);
    }

    [Fact]
    public void Human_resuming_control_de_escalates()
    {
        var d = Eval(Req(resumed: Now.AddMinutes(-1), requested: Now.AddMinutes(-5)), [Env()]);
        Assert.Equal(ContinuityOutcome.Pending, d.Outcome);
        Assert.True(d.DeEscalated);
        // A human who resumed BEFORE the request does not retract it.
        var before = Eval(Req(resumed: Now.AddMinutes(-10), requested: Now.AddMinutes(-5)), [Env()]);
        Assert.Equal(ContinuityOutcome.Authorized, before.Outcome);
    }

    [Fact]
    public void Escalation_delay_makes_a_waiting_request_authorized_later()
    {
        var env = Env(immediate: ContinuityUrgency.Critical, delay: TimeSpan.FromMinutes(30));
        var waiting = Eval(Req(urgency: ContinuityUrgency.High, requested: Now.AddMinutes(-10)), [env]);
        Assert.Equal(ContinuityOutcome.Pending, waiting.Outcome);
        Assert.Equal(Now.AddMinutes(20), waiting.AuthorizedAtUtc);

        var elapsed = Eval(Req(urgency: ContinuityUrgency.High, requested: Now.AddMinutes(-31)), [env]);
        Assert.Equal(ContinuityOutcome.Authorized, elapsed.Outcome);
        Assert.True(elapsed.NotifyHumans);
    }
}
