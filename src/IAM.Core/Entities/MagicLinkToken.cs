namespace IAM.Core.Entities;

public class MagicLinkToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Token { get; set; } = string.Empty;
    public MagicLinkPurpose Purpose { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}

public enum MagicLinkPurpose
{
    Login,
    EmailVerification
}
