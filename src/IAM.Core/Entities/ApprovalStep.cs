namespace IAM.Core.Entities;

/// <summary>
/// Represents a single step in a multi-level approval chain for an access request.
/// Each step can require one or more approvers (quorum support).
/// </summary>
public class ApprovalStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The access request this step belongs to.
    /// </summary>
    public Guid AccessRequestId { get; set; }
    public AccessRequest AccessRequest { get; set; } = null!;

    /// <summary>
    /// Order of this step in the approval chain (1-based).
    /// Steps are processed sequentially; step N+1 only activates after step N is approved.
    /// </summary>
    public int StepOrder { get; set; }

    /// <summary>
    /// Specific user assigned as approver. Null if approval is by role.
    /// </summary>
    public Guid? ApproverId { get; set; }
    public User? Approver { get; set; }

    /// <summary>
    /// Role whose members can approve this step. Null if a specific approver is assigned.
    /// </summary>
    public Guid? ApproverRoleId { get; set; }
    public Role? ApproverRole { get; set; }

    /// <summary>
    /// The user who actually decided on this step (approved or denied).
    /// </summary>
    public Guid? DecidedByUserId { get; set; }
    public User? DecidedByUser { get; set; }

    /// <summary>
    /// Current status of this approval step.
    /// </summary>
    public ApprovalStepStatus Status { get; set; } = ApprovalStepStatus.Pending;

    /// <summary>
    /// Comment provided by the approver when making a decision.
    /// </summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Number of approvals needed for quorum (N of M). Default is 1.
    /// </summary>
    public int QuorumCount { get; set; } = 1;

    /// <summary>
    /// Number of approvals received so far (for quorum tracking).
    /// </summary>
    public int ApprovalsReceived { get; set; } = 0;

    /// <summary>
    /// When the decision was made.
    /// </summary>
    public DateTime? DecidedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Status of an individual approval step.
/// </summary>
public enum ApprovalStepStatus
{
    /// <summary>
    /// Waiting for approver action.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Step has been approved.
    /// </summary>
    Approved = 1,

    /// <summary>
    /// Step has been denied.
    /// </summary>
    Denied = 2,

    /// <summary>
    /// Step was skipped (e.g., auto-approve rule matched).
    /// </summary>
    Skipped = 3
}
