using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

/// <summary>
/// Represents a user's membership in a group.
/// Supports roles within the group (owner, admin, member) and time-limited membership.
/// </summary>
public class GroupMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GroupId { get; set; }
    public Group? Group { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Role within the group: owner, admin, member
    /// </summary>
    [MaxLength(50)]
    public string Role { get; set; } = "member";

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional expiration for time-limited membership (e.g., project teams)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public bool IsActive { get; set; } = true;
}
