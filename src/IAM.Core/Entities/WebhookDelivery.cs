using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

public class WebhookDelivery
{
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }
    public WebhookSubscription? Subscription { get; set; }

    [MaxLength(100)]
    public string EventType { get; set; } = string.Empty;

    public string Payload { get; set; } = "{}"; // JSON payload sent

    public int HttpStatusCode { get; set; }

    [MaxLength(2000)]
    public string? ResponseBody { get; set; }

    public int AttemptNumber { get; set; } = 1;
    public double DurationMs { get; set; }

    public bool Success { get; set; }

    [MaxLength(500)]
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
