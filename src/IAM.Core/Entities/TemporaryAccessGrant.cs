namespace IAM.Core.Entities;

/// <summary>
/// Temporary access grant for time-limited permissions
/// Automatically revokes access after expiration
/// Common use cases: contractors, visitors, temporary staff, emergency access
/// </summary>
public class TemporaryAccessGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Grant name/reason
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Justification for the temporary access
    /// </summary>
    public string Justification { get; set; } = string.Empty;

    /// <summary>
    /// User receiving temporary access
    /// </summary>
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Role granted temporarily
    /// </summary>
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;

    /// <summary>
    /// Tenant context for this grant
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// When access becomes active (UTC)
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// When access expires (UTC)
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Maximum number of times this access can be used (null = unlimited)
    /// </summary>
    public int? MaxUseCount { get; set; }

    /// <summary>
    /// Current use count
    /// </summary>
    public int CurrentUseCount { get; set; } = 0;

    /// <summary>
    /// Whether to send notification when access expires
    /// </summary>
    public bool NotifyOnExpiration { get; set; } = true;

    /// <summary>
    /// Whether to automatically extend if user is actively using access at expiration time
    /// </summary>
    public bool AutoExtendIfActive { get; set; } = false;

    /// <summary>
    /// Extension duration in minutes (if AutoExtendIfActive is true)
    /// </summary>
    public int ExtensionDurationMinutes { get; set; } = 30;

    /// <summary>
    /// Maximum number of extensions allowed
    /// </summary>
    public int MaxExtensions { get; set; } = 2;

    /// <summary>
    /// Current extension count
    /// </summary>
    public int CurrentExtensions { get; set; } = 0;

    /// <summary>
    /// Status of this grant
    /// </summary>
    public TemporaryAccessStatus Status { get; set; } = TemporaryAccessStatus.Pending;

    /// <summary>
    /// When access was first used
    /// </summary>
    public DateTime? FirstUsedAt { get; set; }

    /// <summary>
    /// When access was last used
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// When access was revoked (if manually revoked before expiration)
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// User who revoked access (if manually revoked)
    /// </summary>
    public Guid? RevokedByUserId { get; set; }

    /// <summary>
    /// Reason for manual revocation
    /// </summary>
    public string? RevocationReason { get; set; }

    /// <summary>
    /// User who approved this temporary access grant
    /// </summary>
    public Guid? ApprovedByUserId { get; set; }

    /// <summary>
    /// When the grant was approved
    /// </summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>
    /// Check if grant is currently active
    /// </summary>
    public bool IsActive()
    {
        var now = DateTime.UtcNow;

        // Check status
        if (Status != TemporaryAccessStatus.Active)
            return false;

        // Check time range
        if (now < StartTime || now > EndTime)
            return false;

        // Check use count
        if (MaxUseCount.HasValue && CurrentUseCount >= MaxUseCount.Value)
            return false;

        return true;
    }

    /// <summary>
    /// Check if grant can be extended
    /// </summary>
    public bool CanExtend()
    {
        return AutoExtendIfActive &&
               CurrentExtensions < MaxExtensions &&
               Status == TemporaryAccessStatus.Active;
    }

    /// <summary>
    /// Extend the grant
    /// </summary>
    public bool Extend()
    {
        if (!CanExtend())
            return false;

        EndTime = EndTime.AddMinutes(ExtensionDurationMinutes);
        CurrentExtensions++;
        return true;
    }

    /// <summary>
    /// Record a use of this grant
    /// </summary>
    public void RecordUse()
    {
        CurrentUseCount++;
        LastUsedAt = DateTime.UtcNow;

        if (FirstUsedAt == null)
        {
            FirstUsedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Manually revoke this grant
    /// </summary>
    public void Revoke(Guid revokedByUserId, string reason)
    {
        Status = TemporaryAccessStatus.Revoked;
        RevokedAt = DateTime.UtcNow;
        RevokedByUserId = revokedByUserId;
        RevocationReason = reason;
    }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// Status of temporary access grant
/// </summary>
public enum TemporaryAccessStatus
{
    /// <summary>
    /// Grant created but not yet started (waiting for StartTime)
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Grant is active and can be used
    /// </summary>
    Active = 1,

    /// <summary>
    /// Grant expired naturally (reached EndTime)
    /// </summary>
    Expired = 2,

    /// <summary>
    /// Grant was manually revoked
    /// </summary>
    Revoked = 3,

    /// <summary>
    /// Grant reached maximum use count
    /// </summary>
    UseLimitReached = 4
}
