namespace IAM.Core.Entities;

/// <summary>
/// Represents a delegation of permissions from one user to another.
/// Supports time-bounded, permission-scoped delegation with optional approval workflow.
/// </summary>
public class Delegation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// User who is delegating their permissions.
    /// </summary>
    public Guid DelegatorUserId { get; set; }
    public User DelegatorUser { get; set; } = null!;

    /// <summary>
    /// User who receives the delegated permissions.
    /// </summary>
    public Guid DelegateUserId { get; set; }
    public User DelegateUser { get; set; } = null!;

    /// <summary>
    /// Tenant context for this delegation.
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// JSON array of permission strings being delegated (e.g., ["Building.View", "Room.Manage"]).
    /// </summary>
    public string Permissions { get; set; } = "[]";

    /// <summary>
    /// When the delegation becomes active (UTC).
    /// </summary>
    public DateTime ValidFrom { get; set; }

    /// <summary>
    /// When the delegation expires (UTC).
    /// </summary>
    public DateTime ValidUntil { get; set; }

    /// <summary>
    /// Business reason for the delegation.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Whether the delegation is currently active (not revoked).
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this delegation requires approval before becoming effective.
    /// </summary>
    public bool RequiresApproval { get; set; } = false;

    /// <summary>
    /// Current status of the delegation.
    /// </summary>
    public DelegationStatus Status { get; set; } = DelegationStatus.Active;

    /// <summary>
    /// User who approved the delegation (if approval was required).
    /// </summary>
    public Guid? ApprovedByUserId { get; set; }

    /// <summary>
    /// When the delegation was approved.
    /// </summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>
    /// When the delegation was revoked (if manually revoked).
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// User who revoked the delegation.
    /// </summary>
    public Guid? RevokedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Check if delegation is currently effective (active, approved, within time range).
    /// </summary>
    public bool IsEffective()
    {
        var now = DateTime.UtcNow;

        if (!IsActive || Status != DelegationStatus.Active)
            return false;

        if (now < ValidFrom || now > ValidUntil)
            return false;

        return true;
    }

    /// <summary>
    /// Revoke this delegation.
    /// </summary>
    public void Revoke(Guid revokedByUserId)
    {
        IsActive = false;
        Status = DelegationStatus.Revoked;
        RevokedAt = DateTime.UtcNow;
        RevokedByUserId = revokedByUserId;
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Status of a delegation.
/// </summary>
public enum DelegationStatus
{
    /// <summary>
    /// Delegation is awaiting approval.
    /// </summary>
    PendingApproval = 0,

    /// <summary>
    /// Delegation is active and effective.
    /// </summary>
    Active = 1,

    /// <summary>
    /// Delegation has expired (past ValidUntil).
    /// </summary>
    Expired = 2,

    /// <summary>
    /// Delegation was manually revoked.
    /// </summary>
    Revoked = 3,

    /// <summary>
    /// Delegation approval was denied.
    /// </summary>
    Denied = 4
}
