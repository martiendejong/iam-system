using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing delegations and segregation of duties (SoD) constraints.
/// Handles creating/revoking delegations, checking SoD conflicts, and computing effective permissions.
/// </summary>
public interface IDelegationService
{
    // ─── Delegations ─────────────────────────────────────────

    /// <summary>
    /// Create a new delegation from one user to another. Authority is enforced here (task 4712): only the
    /// delegator themselves, or a global admin on their behalf, may create it, and only for permissions the
    /// delegator holds in the tenant. Throws <see cref="DelegationAccessDeniedException"/> otherwise.
    /// A delegation granting a wildcard permission always requires approval, whatever the flag says.
    /// </summary>
    Task<Delegation> CreateDelegationAsync(
        DelegationActor actor,
        Guid delegatorUserId,
        Guid delegateUserId,
        Guid tenantId,
        List<string> permissions,
        DateTime validFrom,
        DateTime validUntil,
        string reason,
        bool requiresApproval = false,
        CancellationToken ct = default);

    /// <summary>
    /// Get a delegation by ID. Readable by its delegator, its delegate, a global admin and an admin of its
    /// tenant; anyone else gets <see cref="DelegationAccessDeniedException"/>.
    /// </summary>
    Task<Delegation?> GetDelegationAsync(DelegationActor actor, Guid delegationId, CancellationToken ct = default);

    /// <summary>
    /// Get the delegations of a tenant. Global admins and admins of that tenant see all of them; everyone
    /// else only the ones they are the delegator or delegate of.
    /// </summary>
    Task<List<Delegation>> GetDelegationsAsync(DelegationActor actor, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Get active (currently effective) delegations for a delegate user.
    /// </summary>
    Task<List<Delegation>> GetActiveDelegationsForUserAsync(Guid delegateUserId, CancellationToken ct = default);

    /// <summary>
    /// Approve a pending delegation. The approver must be a global admin or an admin of the delegation's
    /// tenant, and never its delegator or delegate. The delegator must still hold the permissions.
    /// </summary>
    Task<Delegation> ApproveDelegationAsync(DelegationActor actor, Guid delegationId, CancellationToken ct = default);

    /// <summary>
    /// Revoke a delegation. Allowed for its delegator, its delegate, a global admin and an admin of its tenant.
    /// </summary>
    Task<Delegation> RevokeDelegationAsync(DelegationActor actor, Guid delegationId, CancellationToken ct = default);

    /// <summary>
    /// Get effective permissions for a user including delegated permissions.
    /// Returns the union of direct role permissions and delegated permissions.
    /// This is the unchecked computation; callers acting for a request use the actor overload.
    /// </summary>
    Task<List<string>> GetEffectivePermissionsAsync(Guid userId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Effective permissions of <paramref name="userId"/> for a request: a user may read their own; reading
    /// another user's needs a global admin or an admin of the tenant.
    /// </summary>
    Task<List<string>> GetEffectivePermissionsAsync(DelegationActor actor, Guid userId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Expire all delegations that have passed their ValidUntil deadline.
    /// </summary>
    Task<int> ExpireDelegationsAsync(CancellationToken ct = default);

    // ─── SoD Constraints ─────────────────────────────────────

    /// <summary>
    /// Create a new SoD constraint.
    /// </summary>
    Task<SodConstraint> CreateConstraintAsync(
        string name,
        Guid tenantId,
        Guid conflictingRoleA,
        Guid conflictingRoleB,
        string description,
        SodSeverity severity = SodSeverity.Warning,
        CancellationToken ct = default);

    /// <summary>
    /// Get a SoD constraint by ID.
    /// </summary>
    Task<SodConstraint?> GetConstraintAsync(Guid constraintId, CancellationToken ct = default);

    /// <summary>
    /// Get all SoD constraints for a tenant.
    /// </summary>
    Task<List<SodConstraint>> GetConstraintsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Update a SoD constraint.
    /// </summary>
    Task<SodConstraint> UpdateConstraintAsync(Guid constraintId, SodConstraint updated, CancellationToken ct = default);

    /// <summary>
    /// Delete a SoD constraint.
    /// </summary>
    Task<bool> DeleteConstraintAsync(Guid constraintId, CancellationToken ct = default);

    /// <summary>
    /// Check for SoD violations for a specific user.
    /// Returns any violations found based on the user's current roles.
    /// </summary>
    Task<List<SodViolation>> CheckSodViolationsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Get all SoD violations for a tenant.
    /// </summary>
    Task<List<SodViolation>> GetViolationsAsync(Guid? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Resolve a SoD violation.
    /// </summary>
    Task<SodViolation> ResolveViolationAsync(Guid violationId, Guid resolvedByUserId, string resolution, CancellationToken ct = default);
}
