using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

public class ApiKey
{
    public Guid Id { get; set; }

    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(128)]
    public string KeyHash { get; set; } = string.Empty; // SHA256 hash of the actual key

    [MaxLength(16)]
    public string KeyPrefix { get; set; } = string.Empty; // First 8 chars for identification (e.g., "iam_k1_a3")

    public Guid? UserId { get; set; } // Owner
    public User? User { get; set; }

    public Guid? TenantId { get; set; } // Scoped to tenant
    public Tenant? Tenant { get; set; }

    public string Permissions { get; set; } = "[]"; // JSONB - list of permission strings

    public string? AllowedIps { get; set; } // JSONB - list of allowed IPs/CIDRs, null = any

    public int? RateLimitPerMinute { get; set; } // Custom rate limit, null = use default

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? Description { get; set; }
}
