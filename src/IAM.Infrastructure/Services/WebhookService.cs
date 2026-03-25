using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Manages webhook subscription CRUD operations and test delivery.
/// </summary>
public class WebhookService : IWebhookService
{
    private readonly IAMDbContext _context;
    private readonly IEventBus _eventBus;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(
        IAMDbContext context,
        IEventBus eventBus,
        ILogger<WebhookService> logger)
    {
        _context = context;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<WebhookSubscription> CreateSubscriptionAsync(WebhookSubscription subscription, CancellationToken ct = default)
    {
        // Validate the URL is well-formed
        if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            throw new ArgumentException("Webhook URL must be a valid absolute HTTP or HTTPS URL.");
        }

        // Validate subscribed events
        if (!string.IsNullOrEmpty(subscription.Events))
        {
            try
            {
                var events = JsonSerializer.Deserialize<List<string>>(subscription.Events);
                if (events != null)
                {
                    foreach (var evt in events)
                    {
                        if (evt != "*" && !evt.EndsWith(".*") && !IamEventTypes.All.Contains(evt))
                        {
                            throw new ArgumentException($"Unknown event type: {evt}. Use GET /api/webhooks/event-types for valid types.");
                        }
                    }
                }
            }
            catch (JsonException)
            {
                throw new ArgumentException("Events must be a valid JSON array of strings.");
            }
        }

        subscription.Id = Guid.NewGuid();
        subscription.CreatedAt = DateTime.UtcNow;
        subscription.UpdatedAt = DateTime.UtcNow;
        subscription.TotalDeliveries = 0;
        subscription.SuccessfulDeliveries = 0;
        subscription.FailedDeliveries = 0;

        _context.Set<WebhookSubscription>().Add(subscription);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Webhook subscription created: {Id} ({Name}) for tenant {TenantId} targeting {Url}",
            subscription.Id, subscription.Name, subscription.TenantId, subscription.Url);

        return subscription;
    }

    public async Task<WebhookSubscription?> GetSubscriptionAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Set<WebhookSubscription>()
            .Include(s => s.Tenant)
            .Include(s => s.CreatedByUser)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<List<WebhookSubscription>> GetSubscriptionsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.Set<WebhookSubscription>()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<WebhookSubscription> UpdateSubscriptionAsync(
        Guid id,
        string? name,
        string? url,
        List<string>? events,
        bool? isActive,
        CancellationToken ct = default)
    {
        var subscription = await _context.Set<WebhookSubscription>()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException($"Webhook subscription {id} not found.");

        if (name != null)
            subscription.Name = name;

        if (url != null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                throw new ArgumentException("Webhook URL must be a valid absolute HTTP or HTTPS URL.");
            }
            subscription.Url = url;
        }

        if (events != null)
        {
            // Validate events
            foreach (var evt in events)
            {
                if (evt != "*" && !evt.EndsWith(".*") && !IamEventTypes.All.Contains(evt))
                {
                    throw new ArgumentException($"Unknown event type: {evt}. Use GET /api/webhooks/event-types for valid types.");
                }
            }
            subscription.Events = JsonSerializer.Serialize(events);
        }

        if (isActive.HasValue)
            subscription.IsActive = isActive.Value;

        subscription.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Webhook subscription updated: {Id} ({Name})",
            subscription.Id, subscription.Name);

        return subscription;
    }

    public async Task<bool> DeleteSubscriptionAsync(Guid id, CancellationToken ct = default)
    {
        var subscription = await _context.Set<WebhookSubscription>()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (subscription == null)
            return false;

        _context.Set<WebhookSubscription>().Remove(subscription);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Webhook subscription deleted: {Id} ({Name})",
            subscription.Id, subscription.Name);

        return true;
    }

    public async Task<WebhookDelivery> TestWebhookAsync(Guid subscriptionId, CancellationToken ct = default)
    {
        var subscription = await _context.Set<WebhookSubscription>()
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new KeyNotFoundException($"Webhook subscription {subscriptionId} not found.");

        _logger.LogInformation(
            "Sending test webhook to subscription {Id} ({Name}) at {Url}",
            subscription.Id, subscription.Name, subscription.Url);

        // Publish a test event - the EventBus will handle delivery and recording
        var testPayload = new
        {
            message = "This is a test webhook delivery from IAM System.",
            subscriptionId = subscription.Id,
            subscriptionName = subscription.Name,
            timestamp = DateTime.UtcNow
        };

        await _eventBus.PublishAsync("webhook.test", testPayload, subscription.TenantId, ct);

        // Return the most recent delivery for this subscription
        var delivery = await _context.Set<WebhookDelivery>()
            .Where(d => d.SubscriptionId == subscriptionId)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // If no delivery was created (e.g., subscription not listening to webhook.test),
        // create a direct test delivery
        if (delivery == null || delivery.EventType != "webhook.test")
        {
            // Temporarily add webhook.test to subscription events so delivery goes through
            var originalEvents = subscription.Events;
            try
            {
                var currentEvents = JsonSerializer.Deserialize<List<string>>(subscription.Events) ?? new List<string>();
                if (!currentEvents.Contains("webhook.test") && !currentEvents.Contains("*"))
                {
                    currentEvents.Add("webhook.test");
                    subscription.Events = JsonSerializer.Serialize(currentEvents);
                    await _context.SaveChangesAsync(ct);

                    await _eventBus.PublishAsync("webhook.test", testPayload, subscription.TenantId, ct);

                    delivery = await _context.Set<WebhookDelivery>()
                        .Where(d => d.SubscriptionId == subscriptionId && d.EventType == "webhook.test")
                        .OrderByDescending(d => d.CreatedAt)
                        .FirstOrDefaultAsync(ct);
                }
            }
            finally
            {
                // Restore original events
                subscription.Events = originalEvents;
                subscription.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
            }
        }

        return delivery ?? new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            EventType = "webhook.test",
            Payload = JsonSerializer.Serialize(testPayload),
            Success = false,
            Error = "Test delivery could not be completed",
            CreatedAt = DateTime.UtcNow
        };
    }
}
