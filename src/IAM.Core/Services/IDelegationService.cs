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
    /// Create a new delegation from one user to another.
    /// </summary>
    Task<Delegation> CreateDelegationAsync(
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
    /// Get a delegation by ID.
    /// </summary>
    Task<Delegation?> GetDelegationAsync(Guid delegationId, CancellationToken ct = default);

    /// <summary>
    /// Get all delegations for a tenant.
    /// </summary>
    Task<List<Delegation>> GetDelegationsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Get active (currently effective) delegations for a delegate user.
    /// </summary>
    Task<List<Delegation>> GetActiveDelegationsForUserAsync(Guid delegateUserId, CancellationToken ct = default);

    /// <summary>
    /// Approve a pending delegation.
    /// </summary>
    Task<Delegation> ApproveDelegationAsync(Guid delegationId, Guid approverUserId, CancellationToken ct = default);

    /// <summary>
    /// Revoke a delegation.
    /// </summary>
    Task<Delegation> RevokeDelegationAsync(Guid delegationId, Guid revokedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Get effective permissions for a user including delegated permissions.
    /// Returns the union of direct role permissions and delegated permissions.
    /// </summary>
    Task<List<string>> GetEffectivePermissionsAsync(Guid userId, Guid tenantId, CancellationToken ct = default);

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
