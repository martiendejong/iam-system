using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

public class SecretEntry
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string EncryptedValue { get; set; } = string.Empty; // AES-256 encrypted, Base64 encoded

    [MaxLength(32)]
    public string IV { get; set; } = string.Empty; // AES initialization vector, Base64 encoded

    public string? RotationSchedule { get; set; } // JSONB - cron expression, interval, grace period config

    public DateTime? LastRotatedAt { get; set; }

    public DateTime? NextRotationAt { get; set; }

    public int Version { get; set; } = 1;

    [Required, MaxLength(50)]
    public string SecretType { get; set; } = "Generic"; // Generic, ApiKey, DatabasePassword, Certificate, OAuth, ConnectionString

    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(500)]
    public string? Tags { get; set; } // JSONB - tags for categorization

    public Guid? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SecretVersion
{
    public Guid Id { get; set; }

    public Guid SecretEntryId { get; set; }
    public SecretEntry? SecretEntry { get; set; }

    [Required]
    public string EncryptedValue { get; set; } = string.Empty;

    [MaxLength(32)]
    public string IV { get; set; } = string.Empty;

    public int Version { get; set; }

    [MaxLength(50)]
    public string RotationReason { get; set; } = "Manual"; // Manual, Scheduled, Emergency, Expired

    public Guid? RotatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Grace period end - old version remains valid until this time.
    /// Allows consuming services to pick up the new secret before the old one is invalidated.
    /// </summary>
    public DateTime? GracePeriodEndsAt { get; set; }

    public bool IsRevoked { get; set; }
}
