namespace IAM.Core.Entities;

// Delegated Authority epic, task 4843: persistent multi-tenant Chain of Command data model.
// These entities REFERENCE existing IAM identities (User, Group, Role, ServiceAccount) by id and
// never copy RBAC permissions. History is kept in AuthorityRecordVersion snapshots so a past
// decision can be reconstructed as "what chain applied at time T, at which version".

/// <summary>Level of a policy scope. Inheritance runs organization, project, resource, action.</summary>
public enum AuthorityScopeLevel
{
    Organization = 0,
    Project = 1,
    Resource = 2,
    Action = 3
}

/// <summary>Kind of principal that can sit in a command stage.</summary>
public enum CommandStageMemberKind
{
    User = 0,
    Group = 1,
    Role = 2,
    ExternalAuthority = 3,
    Agent = 4
}

public enum AuthorityDelegationStatus
{
    Active = 0,
    Revoked = 1,
    Expired = 2
}

/// <summary>A named area of decision authority within a tenant (for example "Production deploys").</summary>
public class AuthorityDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Stable machine key, unique per tenant.</summary>
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;
    /// <summary>Incremented on every change; each version is snapshotted in AuthorityRecordVersion.</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AuthorityPolicy> Policies { get; set; } = new List<AuthorityPolicy>();
}

/// <summary>
/// Binds a scope (organization, project, resource or action) in a domain to the command chain that
/// decides there. A narrower scope inherits the nearest wider one unless it is an explicit override.
/// </summary>
public class AuthorityPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid AuthorityDomainId { get; set; }
    public AuthorityDomain? AuthorityDomain { get; set; }

    public AuthorityScopeLevel ScopeLevel { get; set; } = AuthorityScopeLevel.Organization;
    /// <summary>Project key; required from Project level down, null at Organization level.</summary>
    public string? ProjectKey { get; set; }
    /// <summary>Resource type and id; required from Resource level down.</summary>
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    /// <summary>Action name; required at Action level.</summary>
    public string? Action { get; set; }

    /// <summary>
    /// True: this policy replaces whatever a narrower scope says.
    /// False: the policy is a default that a narrower policy may supersede.
    /// </summary>
    public bool IsExplicitOverride { get; set; }

    public Guid CommandChainId { get; set; }
    public CommandChain? CommandChain { get; set; }

    /// <summary>Reference (id, URL or key) to a continuity policy held elsewhere; not interpreted here.</summary>
    public string? ContinuityPolicyRef { get; set; }

    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveUntil { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsEffectiveAt(DateTime utc) =>
        IsEnabled && utc >= EffectiveFrom && (EffectiveUntil is null || utc < EffectiveUntil);
}

/// <summary>An ordered set of stages that a decision escalates through.</summary>
public class CommandChain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveUntil { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CommandStage> Stages { get; set; } = new List<CommandStage>();
}

public class CommandStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommandChainId { get; set; }
    public CommandChain? CommandChain { get; set; }

    /// <summary>1-based position in the chain; unique per chain.</summary>
    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>How many members must act; at least 1.</summary>
    public int RequiredApprovals { get; set; } = 1;

    /// <summary>Seconds without a decision before the next stage is engaged; null means never.</summary>
    public int? EscalationTimeoutSeconds { get; set; }
    /// <summary>JSON describing extra escalation conditions (member unavailable, risk level, ...).</summary>
    public string EscalationConditions { get; set; } = "{}";

    public ICollection<CommandStageMember> Members { get; set; } = new List<CommandStageMember>();
}

/// <summary>Exactly one reference column is set, matching <see cref="Kind"/>.</summary>
public class CommandStageMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommandStageId { get; set; }
    public CommandStage? CommandStage { get; set; }

    public CommandStageMemberKind Kind { get; set; }
    public Guid? UserId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? RoleId { get; set; }
    /// <summary>Agent identity (ServiceAccount with PrincipalKind Agent).</summary>
    public Guid? ServiceAccountId { get; set; }
    /// <summary>Authority outside IAM (a court, regulator, notary, ...) as a free reference.</summary>
    public string? ExternalAuthorityRef { get; set; }

    public bool HasExactlyOneReferenceMatchingKind()
    {
        var set = new[]
        {
            UserId.HasValue, GroupId.HasValue, RoleId.HasValue,
            ServiceAccountId.HasValue, !string.IsNullOrWhiteSpace(ExternalAuthorityRef)
        };
        if (set.Count(x => x) != 1) return false;
        return Kind switch
        {
            CommandStageMemberKind.User => UserId.HasValue,
            CommandStageMemberKind.Group => GroupId.HasValue,
            CommandStageMemberKind.Role => RoleId.HasValue,
            CommandStageMemberKind.Agent => ServiceAccountId.HasValue,
            CommandStageMemberKind.ExternalAuthority => !string.IsNullOrWhiteSpace(ExternalAuthorityRef),
            _ => false
        };
    }
}

/// <summary>Authority handed from one user to another inside a domain (optionally one stage).</summary>
public class AuthorityDelegation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid AuthorityDomainId { get; set; }
    public AuthorityDomain? AuthorityDomain { get; set; }
    public Guid? CommandStageId { get; set; }

    public Guid DelegatorUserId { get; set; }
    public Guid DelegateUserId { get; set; }
    /// <summary>Optional link to the permission-level Delegation this authority grant accompanies.</summary>
    public Guid? DelegationId { get; set; }

    public string Reason { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveUntil { get; set; }
    public AuthorityDelegationStatus Status { get; set; } = AuthorityDelegationStatus.Active;
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Pointer to evidence (heartbeat, calendar entry, ticket) that a member was or was not available.</summary>
public class AuthorityAvailabilityEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid? CommandStageMemberId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>Source system, for example "heartbeat", "calendar" or "manual".</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>Reference into the source system; the evidence itself is not stored here.</summary>
    public string EvidenceRef { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ValidUntil { get; set; }
}

/// <summary>Append-only snapshot of a versioned authority record, so past decisions can be reconstructed.</summary>
public class AuthorityRecordVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>"AuthorityDomain", "AuthorityPolicy" or "CommandChain".</summary>
    public string RecordType { get; set; } = string.Empty;
    public Guid RecordId { get; set; }
    public int Version { get; set; }
    /// <summary>Full JSON of the record (a chain includes its stages and members) at this version.</summary>
    public string Snapshot { get; set; } = "{}";
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public Guid? ChangedByUserId { get; set; }
    public string? ChangeReason { get; set; }
}
