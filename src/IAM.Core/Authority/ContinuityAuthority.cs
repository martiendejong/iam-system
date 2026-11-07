namespace IAM.Core.Authority;

// Delegated Authority epic, task 4846: bounded autonomous continuity authority.
// Pure, side-effect free policy semantics. Callers (API / worker) load the pre-authorized
// envelopes and hard constraints, call ContinuityAuthorityEvaluator.Evaluate and act on the decision.

/// <summary>How urgent the requested action is. Affects escalation timing only, never hard denies.</summary>
public enum ContinuityUrgency
{
    Normal = 0,
    High = 1,
    Critical = 2
}

/// <summary>The kind of action being requested. Used to match an envelope.</summary>
public enum ContinuityActionKind
{
    /// <summary>Safe restart / rollback / remediation of a service that is down or degraded.</summary>
    OutageRemediation = 0,
    /// <summary>Production bugfix.</summary>
    ProductionBugfix = 1,
    /// <summary>Non-urgent feature release.</summary>
    FeatureRelease = 2,
    Other = 3
}

/// <summary>The authority artifacts an agent must never be able to change for itself.</summary>
public enum AuthorityArtifact
{
    None = 0,
    Policy = 1,
    CommandChain = 2,
    EvidenceRequirements = 3,
    HardBoundary = 4,
    ContinuityEnvelope = 5
}

public enum ContinuityOutcome
{
    /// <summary>Within an active, scoped envelope with all conditions met.</summary>
    Authorized = 0,
    /// <summary>No continuity authority applies (yet); normal chain of command decides.</summary>
    Pending = 1,
    /// <summary>Explicitly forbidden (hard deny, self-modification, or human-only domain).</summary>
    Denied = 2
}

/// <summary>A scope pattern. A null/empty field matches anything; otherwise ordinal-ignore-case equality.</summary>
public sealed record ContinuityScope(string? ProjectKey, string? ResourceType, string? ResourceId, string? Action)
{
    public bool Covers(ContinuityScope requested) =>
        Part(ProjectKey, requested.ProjectKey) && Part(ResourceType, requested.ResourceType) &&
        Part(ResourceId, requested.ResourceId) && Part(Action, requested.Action);

    private static bool Part(string? pattern, string? value) =>
        string.IsNullOrEmpty(pattern) || string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A pre-authorized envelope for ONE concrete domain/resource/action. Authored by humans; the agent
/// may only be handed it, never edit it. Wildcard scopes (all parts empty) are never valid.
/// </summary>
public sealed record ContinuityEnvelope(
    Guid Id,
    Guid TenantId,
    Guid AuthorityDomainId,
    Guid AgentId,
    ContinuityActionKind Kind,
    ContinuityScope Scope,
    DateTime EffectiveFrom,
    DateTime? EffectiveUntil,
    /// <summary>Urgency at which the envelope applies without waiting (e.g. Critical for outages).</summary>
    ContinuityUrgency ImmediateFromUrgency,
    /// <summary>Evidence keys that must all be present (tests passed, rollback plan, ...).</summary>
    IReadOnlySet<string> RequiredEvidence,
    bool RequiresReversible,
    /// <summary>How long a non-immediate request waits for a human before the envelope applies. Null = never auto-applies.</summary>
    TimeSpan? EscalationDelay,
    bool IsEnabled = true)
{
    public bool IsEffectiveAt(DateTime utc) => IsEnabled && utc >= EffectiveFrom && (EffectiveUntil is null || utc < EffectiveUntil);

    public string? Validate()
    {
        if (string.IsNullOrEmpty(Scope.ProjectKey) && string.IsNullOrEmpty(Scope.ResourceType) &&
            string.IsNullOrEmpty(Scope.ResourceId) && string.IsNullOrEmpty(Scope.Action))
            return "A continuity envelope must be scoped to a concrete domain/resource/action; a blanket scope is not allowed.";
        if (string.IsNullOrEmpty(Scope.Action))
            return "A continuity envelope must name the action it covers.";
        if (EffectiveUntil is null)
            return "A continuity envelope must expire.";
        if (EscalationDelay is { } d && d < TimeSpan.Zero)
            return "EscalationDelay cannot be negative.";
        return null;
    }
}

/// <summary>A configurable hard human-only constraint (finance, legal, security boundary, ...). Always wins.</summary>
public sealed record HardHumanOnlyConstraint(Guid TenantId, string Name, ContinuityScope Scope, bool IsEnabled = true);

/// <summary>A request by an agent to act while human command is unavailable.</summary>
public sealed record ContinuityRequest(
    Guid TenantId,
    Guid AuthorityDomainId,
    Guid AgentId,
    ContinuityActionKind Kind,
    ContinuityScope Scope,
    ContinuityUrgency Urgency,
    bool IsReversible,
    IReadOnlySet<string> EvidenceProvided,
    DateTime RequestedAtUtc,
    DateTime EvaluatedAtUtc,
    /// <summary>When a valid higher-priority human authority last took (back) control, if ever.</summary>
    DateTime? HumanControlResumedAtUtc = null,
    /// <summary>If the request changes an authority artifact. Anything but None is denied for an agent.</summary>
    AuthorityArtifact Target = AuthorityArtifact.None);

public sealed record ContinuityDecision(
    ContinuityOutcome Outcome,
    string Reason,
    Guid? EnvelopeId = null,
    /// <summary>Humans must be notified in parallel whenever continuity authority is used.</summary>
    bool NotifyHumans = false,
    /// <summary>When Pending: the moment the envelope would start to apply if no human has responded.</summary>
    DateTime? AuthorizedAtUtc = null,
    IReadOnlyList<string>? MissingEvidence = null,
    /// <summary>True when authority was withdrawn because a human resumed control.</summary>
    bool DeEscalated = false);

public static class ContinuityAuthorityEvaluator
{
    public static ContinuityDecision Evaluate(
        ContinuityRequest request,
        IEnumerable<ContinuityEnvelope> envelopes,
        IEnumerable<HardHumanOnlyConstraint> hardConstraints)
    {
        // 1. An agent can never change the policy, chain, evidence requirements or boundary behind its own authority.
        if (request.Target != AuthorityArtifact.None)
            return new(ContinuityOutcome.Denied,
                $"An agent cannot modify authority artifact '{request.Target}'.");

        // 2. Hard human-only constraints beat everything: risk, urgency and reversibility are irrelevant.
        var hard = hardConstraints.FirstOrDefault(c =>
            c.IsEnabled && c.TenantId == request.TenantId && c.Scope.Covers(request.Scope));
        if (hard is not null)
            return new(ContinuityOutcome.Denied, $"Hard human-only constraint '{hard.Name}' applies.");

        // 3. Find an effective, scoped envelope for this exact agent/domain/kind.
        var candidates = envelopes.Where(e =>
            e.TenantId == request.TenantId && e.AuthorityDomainId == request.AuthorityDomainId &&
            e.AgentId == request.AgentId && e.Kind == request.Kind && e.Validate() is null &&
            e.IsEffectiveAt(request.EvaluatedAtUtc) && e.Scope.Covers(request.Scope)).ToList();
        if (candidates.Count == 0)
            return new(ContinuityOutcome.Pending, "No continuity envelope covers this action; the normal chain of command decides.");

        var env = candidates.OrderByDescending(e => e.EffectiveFrom).First();

        // 4. De-escalate when a human resumed control after the request started.
        if (request.HumanControlResumedAtUtc is { } resumed && resumed >= request.RequestedAtUtc)
            return new(ContinuityOutcome.Pending, "A higher-priority human authority resumed control; continuity authority withdrawn.",
                env.Id, DeEscalated: true);

        // 5. Conditions: reversibility and evidence.
        if (env.RequiresReversible && !request.IsReversible)
            return new(ContinuityOutcome.Pending, "The envelope only covers reversible actions.", env.Id);
        var missing = env.RequiredEvidence.Where(k => !request.EvidenceProvided.Contains(k)).OrderBy(k => k).ToList();
        if (missing.Count > 0)
            return new(ContinuityOutcome.Pending, "Required evidence is missing.", env.Id, MissingEvidence: missing);

        // 6. Urgency only changes WHEN the envelope applies.
        if (request.Urgency >= env.ImmediateFromUrgency)
            return new(ContinuityOutcome.Authorized, "Continuity authority applies immediately; notify humans in parallel.",
                env.Id, NotifyHumans: true, AuthorizedAtUtc: request.EvaluatedAtUtc);

        if (env.EscalationDelay is { } delay)
        {
            var at = request.RequestedAtUtc + delay;
            return at <= request.EvaluatedAtUtc
                ? new(ContinuityOutcome.Authorized, "No human responded within the escalation delay.", env.Id, true, at)
                : new(ContinuityOutcome.Pending, "Waiting for a human until the escalation delay passes.", env.Id, AuthorizedAtUtc: at);
        }

        return new(ContinuityOutcome.Pending, "Not urgent enough for continuity authority; remains pending.", env.Id);
    }
}
