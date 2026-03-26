namespace IAM.Core.Entities;

/// <summary>
/// Represents a visitor who requires temporary physical or digital access.
/// Tracks visitor pre-registration, check-in/out, and QR-code-based identification.
/// </summary>
public class Visitor
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Full name of the visitor
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Visitor's email address (for sending QR code / access instructions)
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Company or organization the visitor represents
    /// </summary>
    public string? Company { get; set; }

    /// <summary>
    /// The internal user who is hosting this visitor
    /// </summary>
    public Guid HostUserId { get; set; }
    public User? HostUser { get; set; }

    /// <summary>
    /// Tenant scope for this visitor
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Scheduled date of the visit
    /// </summary>
    public DateTime VisitDate { get; set; }

    /// <summary>
    /// When the visitor actually checked in (null = not yet checked in)
    /// </summary>
    public DateTime? CheckInAt { get; set; }

    /// <summary>
    /// When the visitor checked out (null = still on-site or not yet visited)
    /// </summary>
    public DateTime? CheckOutAt { get; set; }

    /// <summary>
    /// Current status of the visitor
    /// </summary>
    public VisitorStatus Status { get; set; } = VisitorStatus.PreRegistered;

    /// <summary>
    /// Unique token used to generate and validate the visitor's QR code
    /// </summary>
    public string QrToken { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Optional notes or purpose of visit
    /// </summary>
    public string? Purpose { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property - access grants for this visitor
    /// </summary>
    public List<VisitorAccessGrant> AccessGrants { get; set; } = new();
}

/// <summary>
/// Status of a visitor registration
/// </summary>
public enum VisitorStatus
{
    /// <summary>
    /// Visitor has been pre-registered but has not arrived yet
    /// </summary>
    PreRegistered = 0,

    /// <summary>
    /// Visitor has checked in and is currently on-site
    /// </summary>
    CheckedIn = 1,

    /// <summary>
    /// Visitor has checked out
    /// </summary>
    CheckedOut = 2,

    /// <summary>
    /// Visitor registration was cancelled
    /// </summary>
    Cancelled = 3,

    /// <summary>
    /// Visitor did not show up for the scheduled visit
    /// </summary>
    NoShow = 4
}
