using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IEventBus
{
    Task PublishAsync(string eventType, object payload, Guid? tenantId = null, CancellationToken ct = default);
    Task<List<WebhookDelivery>> GetDeliveryHistoryAsync(Guid subscriptionId, int limit = 50, CancellationToken ct = default);
}

public interface IWebhookService
{
    Task<WebhookSubscription> CreateSubscriptionAsync(WebhookSubscription subscription, CancellationToken ct = default);
    Task<WebhookSubscription?> GetSubscriptionAsync(Guid id, CancellationToken ct = default);
    Task<List<WebhookSubscription>> GetSubscriptionsAsync(Guid tenantId, CancellationToken ct = default);
    Task<WebhookSubscription> UpdateSubscriptionAsync(Guid id, string? name, string? url, List<string>? events, bool? isActive, CancellationToken ct = default);
    Task<bool> DeleteSubscriptionAsync(Guid id, CancellationToken ct = default);
    Task<WebhookDelivery> TestWebhookAsync(Guid subscriptionId, CancellationToken ct = default);
}
