namespace IAM.Core.Entities;

/// <summary>
/// Represents a request for access to a resource, role, or permission.
/// Follows a workflow-driven approval process with single or multi-level approval chains.
/// </summary>
public class AccessRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// User who submitted the access request.
    /// </summary>
    public Guid RequesterId { get; set; }
    public User Requester { get; set; } = null!;

    /// <summary>
    /// Type of resource being requested (e.g., "Role", "Building", "Device", "Permission").
    /// Used to match workflow templates.
    /// </summary>
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the specific resource being requested (e.g., a building ID, device ID).
    /// Null when requesting a role assignment without a specific resource.
    /// </summary>
    public Guid? ResourceId { get; set; }

    /// <summary>
    /// Role being requested (if applicable).
    /// </summary>
    public Guid? RoleId { get; set; }
    public Role? Role { get; set; }

    /// <summary>
    /// Tenant context for this request.
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Business justification for the access request.
    /// </summary>
    public string Justification { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the request.
    /// </summary>
    public AccessRequestStatus Status { get; set; } = AccessRequestStatus.Pending;

    /// <summary>
    /// Priority level for the request (Low, Normal, High, Critical).
    /// </summary>
    public AccessRequestPriority Priority { get; set; } = AccessRequestPriority.Normal;

    /// <summary>
    /// Reference to the workflow template used to generate approval steps.
    /// Null if no matching template was found (falls back to default).
    /// </summary>
    public Guid? WorkflowTemplateId { get; set; }
    public WorkflowTemplate? WorkflowTemplate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this request expires if not acted upon.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Navigation property for the approval chain steps.
    /// </summary>
    public ICollection<ApprovalStep> ApprovalSteps { get; set; } = new List<ApprovalStep>();
}

/// <summary>
/// Status of an access request.
/// </summary>
public enum AccessRequestStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    Expired = 3,
    Cancelled = 4
}

/// <summary>
/// Priority level for access requests.
/// </summary>
public enum AccessRequestPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}
