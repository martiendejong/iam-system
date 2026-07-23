namespace IAM.Core.Entities;

/// <summary>
/// Privileged Access Management (PAM) session for time-boxed privilege elevation.
/// Tracks check-out/check-in of privileged roles with full audit trail.
/// </summary>
public class PrivilegedSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// User requesting privileged access
    /// </summary>
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Privileged role being checked out
    /// </summary>
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;

    /// <summary>
    /// Tenant context for this privileged session
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// When the privileged session was granted/approved
    /// </summary>
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the privileged session expires (auto de-escalation)
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the user checked out (activated) the privileged role
    /// </summary>
    public DateTime? CheckedOutAt { get; set; }

    /// <summary>
    /// When the user checked in (released) the privileged role
    /// </summary>
    public DateTime? CheckedInAt { get; set; }

    /// <summary>
    /// Justification for requesting privileged access
    /// </summary>
    public string Justification { get; set; } = string.Empty;

    /// <summary>
    /// Current session status
    /// </summary>
    public PrivilegedSessionStatus Status { get; set; } = PrivilegedSessionStatus.Pending;

    /// <summary>
    /// User who approved this session (null if auto-approved or pending)
    /// </summary>
    public Guid? ApprovedByUserId { get; set; }

    /// <summary>
    /// Whether this session was created via break-glass emergency access
    /// </summary>
    public bool IsBreakGlass { get; set; }

    /// <summary>
    /// For break-glass: JSON array of approver user IDs who confirmed
    /// </summary>
    public string? BreakGlassApprovers { get; set; }

    /// <summary>
    /// IP address of the requester
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// Correlation ID for linking to audit log entries during this session
    /// </summary>
    public string? AuditCorrelationId { get; set; }

    /// <summary>
    /// Reference to the PAM policy that governed this session
    /// </summary>
    public Guid? PamPolicyId { get; set; }
    public PamPolicy? PamPolicy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Check if session is currently active (checked out and not expired)
    /// </summary>
    public bool IsActive()
    {
        return Status == PrivilegedSessionStatus.Active &&
               CheckedOutAt.HasValue &&
               !CheckedInAt.HasValue &&
               DateTime.UtcNow < ExpiresAt;
    }

    /// <summary>
    /// Check in (release) the privileged role
    /// </summary>
    public void CheckIn()
    {
        Status = PrivilegedSessionStatus.CheckedIn;
        CheckedInAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Mark session as expired (auto de-escalation)
    /// </summary>
    public void Expire()
    {
        Status = PrivilegedSessionStatus.Expired;
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Status of a privileged access session
/// </summary>
public enum PrivilegedSessionStatus
{
    /// <summary>
    /// Session requested, awaiting approval
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Session approved and privileges are active
    /// </summary>
    Active = 1,

    /// <summary>
    /// Session expired (auto de-escalation after timeout)
    /// </summary>
    Expired = 2,

    /// <summary>
    /// User voluntarily checked in / released privileges
    /// </summary>
    CheckedIn = 3,

    /// <summary>
    /// Session was denied by approver
    /// </summary>
    Denied = 4,

    /// <summary>
    /// Session was revoked by administrator
    /// </summary>
    Revoked = 5
}
