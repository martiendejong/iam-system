using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

public class WebhookSubscription
{
    public Guid Id { get; set; }

    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(2000)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Secret { get; set; } // For HMAC signature verification

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    public string Events { get; set; } = "[]"; // JSONB - list of event types to subscribe to

    public string? Headers { get; set; } // JSONB - custom headers to include

    public bool IsActive { get; set; } = true;

    [MaxLength(50)]
    public string ContentType { get; set; } = "application/json";

    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Delivery tracking
    public int TotalDeliveries { get; set; }
    public int SuccessfulDeliveries { get; set; }
    public int FailedDeliveries { get; set; }
    public DateTime? LastDeliveryAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }

    public ICollection<WebhookDelivery> Deliveries { get; set; } = new List<WebhookDelivery>();
}
