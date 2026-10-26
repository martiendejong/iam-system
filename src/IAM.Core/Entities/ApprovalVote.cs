namespace IAM.Core.Entities;

/// <summary>
/// One approver's approval of one <see cref="ApprovalStep"/> (task 4711). A unique index on
/// (ApprovalStepId, UserId) is what makes a quorum count distinct people: the same user cannot
/// approve the same step twice, even under concurrent requests.
/// </summary>
public class ApprovalVote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ApprovalStepId { get; set; }
    public ApprovalStep ApprovalStep { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string? Comment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
