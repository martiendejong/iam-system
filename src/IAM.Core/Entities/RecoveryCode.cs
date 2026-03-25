using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

public class RecoveryCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    [MaxLength(255)]
    public string CodeHash { get; set; } = string.Empty; // BCrypt hash

    public bool IsUsed { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
