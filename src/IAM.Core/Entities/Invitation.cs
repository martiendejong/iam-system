namespace IAM.Core.Entities;

public class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;

    /// <summary>
    /// Unique token used for the invitation link
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Status of the invitation: Pending, Accepted, Expired, Revoked
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// User who sent the invitation
    /// </summary>
    public Guid InvitedByUserId { get; set; }
    public User InvitedByUser { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
