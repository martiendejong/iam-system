namespace IAM.Core.Entities;

public class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string Code { get; set; } = string.Empty;
    public OtpPurpose Purpose { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public User? User { get; set; }
}

public enum OtpPurpose
{
    Login,
    MfaVerification,
    PhoneVerification
}
