using IAM.Core.Entities;

namespace IAM.Core.Authority;

// Task 4844: contract of the canonical Authority Resolver. The engine is pure (no I/O): the service
// gathers facts, the engine turns facts into a decision, so every rule is unit-testable.

/// <summary>Explicit outcome of an authority resolution.</summary>
public enum AuthorityDecisionState
{
    Allow,
    Deny,
    RequiresApproval,
    Escalate,
    ContinuityAuthorized
}

public enum AuthorityLevel
{
    Unspecified = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum HumanAvailability
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2
}

/// <summary>Situation of the request; every field is optional.</summary>
public sealed class AuthorityRequestContext
{
    public AuthorityLevel Urgency { get; set; }
    public AuthorityLevel Severity { get; set; }
    /// <summary>False means the action cannot be undone; null means unknown.</summary>
    public bool? Reversible { get; set; }
    /// <summary>References to evidence (ticket, alert, heartbeat) supporting the request.</summary>
    public List<string> Evidence { get; set; } = new();
    /// <summary>Caller's statement about whether humans in the chain can be reached.</summary>
    public HumanAvailability HumanAvailability { get; set; }
    /// <summary>1-based stage the decision is currently waiting at; default is the first stage.</summary>
    public int? CurrentStageOrder { get; set; }
    /// <summary>When the current stage was engaged; with the stage timeout it gives the deadline.</summary>
    public DateTime? StageEngagedAtUtc { get; set; }
    /// <summary>Free attributes handed to the ABAC evaluation.</summary>
    public Dictionary<string, object>? Attributes { get; set; }
}

public sealed class AuthorityRequest
{
    public Guid TenantId { get; set; }
    public string DomainKey { get; set; } = string.Empty;
    public string? ProjectKey { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? Action { get; set; }
    /// <summary>The user asking for the decision (the requester/actor).</summary>
    public Guid ActorUserId { get; set; }
    public AuthorityRequestContext Context { get; set; } = new();
    /// <summary>Evaluation moment; null means now. Lets a past decision be reconstructed.</summary>
    public DateTime? AtUtc { get; set; }
}

/// <summary>Machine-readable reason. <see cref="Code"/> is stable; <see cref="Message"/> is for humans.</summary>
public sealed record AuthorityReason(string Code, string Message);

/// <summary>One holder of authority; users reached via a group or role are listed individually.</summary>
public sealed record AuthorityHolder(
    CommandStageMemberKind Kind,
    Guid? UserId,
    Guid? GroupId,
    Guid? RoleId,
    Guid? ServiceAccountId,
    string? ExternalAuthorityRef,
    /// <summary>"member", "group", "role" or "delegation".</summary>
    string Via,
    Guid? DelegationId = null);

public sealed record AuthorityStageInfo(
    Guid StageId, int Order, string Name, int RequiredApprovals, int? EscalationTimeoutSeconds,
    IReadOnlyList<AuthorityHolder> Holders);

public sealed record AppliedPolicyInfo(
    Guid PolicyId, int PolicyVersion, Guid ChainId, int ChainVersion, Guid DomainId, int DomainVersion,
    AuthorityScopeLevel ScopeLevel, bool IsExplicitOverride);

public sealed class AuthorityResolution
{
    public AuthorityDecisionState State { get; set; }
    public List<AuthorityReason> Reasons { get; set; } = new();
    public AppliedPolicyInfo? Policy { get; set; }
    /// <summary>The stage whose members decide now.</summary>
    public AuthorityStageInfo? Stage { get; set; }
    /// <summary>Who holds effective authority at <see cref="Stage"/> (empty for Deny).</summary>
    public List<AuthorityHolder> Authorities { get; set; } = new();
    public int RequiredApprovals { get; set; }
    /// <summary>Stage the decision moves to when this one does not act (null at the last stage).</summary>
    public AuthorityStageInfo? EscalationTarget { get; set; }
    /// <summary>When the current stage times out, if it has a timeout and an engage time.</summary>
    public DateTime? DeadlineUtc { get; set; }
    public string? ContinuityPolicyRef { get; set; }
    public DateTime EvaluatedAtUtc { get; set; }
    public string ResolverVersion { get; set; } = AuthorityDecisionEngine.ResolverVersion;
}
