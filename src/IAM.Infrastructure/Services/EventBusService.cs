using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Event bus implementation that delivers webhook notifications to subscribed endpoints.
/// Supports HMAC-SHA256 signature verification, exponential backoff retries, and delivery tracking.
/// </summary>
public class EventBusService : IEventBus
{
    private readonly IAMDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EventBusService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public EventBusService(
        IAMDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<EventBusService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task PublishAsync(string eventType, object payload, Guid? tenantId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            _logger.LogWarning("PublishAsync called with empty event type, skipping");
            return;
        }

        // Find all active subscriptions that listen to this event type for the given tenant
        var subscriptionsQuery = _context.Set<WebhookSubscription>()
            .Where(s => s.IsActive);

        if (tenantId.HasValue)
        {
            subscriptionsQuery = subscriptionsQuery.Where(s => s.TenantId == tenantId.Value);
        }

        var subscriptions = await subscriptionsQuery.ToListAsync(ct);

        // Filter subscriptions that are subscribed to this specific event type
        var matchingSubscriptions = subscriptions
            .Where(s => IsSubscribedToEvent(s, eventType))
            .ToList();

        if (matchingSubscriptions.Count == 0)
        {
            _logger.LogDebug("No active subscriptions found for event {EventType} in tenant {TenantId}", eventType, tenantId);
            return;
        }

        _logger.LogInformation(
            "Publishing event {EventType} to {Count} subscriptions for tenant {TenantId}",
            eventType, matchingSubscriptions.Count, tenantId);

        // Build the envelope payload once
        var envelope = new
        {
            id = Guid.NewGuid(),
            eventType,
            timestamp = DateTime.UtcNow,
            tenantId,
            data = payload
        };

        var payloadJson = JsonSerializer.Serialize(envelope, JsonOptions);

        // Deliver to each subscription (fire-and-forget per subscription, but await all)
        var deliveryTasks = matchingSubscriptions.Select(sub => DeliverWithRetriesAsync(sub, eventType, payloadJson, ct));
        await Task.WhenAll(deliveryTasks);
    }

    public async Task<List<WebhookDelivery>> GetDeliveryHistoryAsync(Guid subscriptionId, int limit = 50, CancellationToken ct = default)
    {
        return await _context.Set<WebhookDelivery>()
            .Where(d => d.SubscriptionId == subscriptionId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(Math.Min(limit, 200))
            .ToListAsync(ct);
    }

    private static bool IsSubscribedToEvent(WebhookSubscription subscription, string eventType)
    {
        try
        {
            var subscribedEvents = JsonSerializer.Deserialize<List<string>>(subscription.Events);
            if (subscribedEvents == null || subscribedEvents.Count == 0)
                return false;

            // Support wildcard subscriptions (e.g., "user.*" matches "user.created")
            return subscribedEvents.Any(e =>
                string.Equals(e, "*", StringComparison.Ordinal) ||
                string.Equals(e, eventType, StringComparison.OrdinalIgnoreCase) ||
                (e.EndsWith(".*") && eventType.StartsWith(e[..^2], StringComparison.OrdinalIgnoreCase)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task DeliverWithRetriesAsync(
        WebhookSubscription subscription,
        string eventType,
        string payloadJson,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, subscription.MaxRetries + 1); // At least 1 attempt

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                break;

            var delivery = await DeliverAsync(subscription, eventType, payloadJson, attempt, ct);

            // Update subscription counters
            subscription.TotalDeliveries++;
            subscription.LastDeliveryAt = DateTime.UtcNow;

            if (delivery.Success)
            {
                subscription.SuccessfulDeliveries++;
                subscription.LastSuccessAt = DateTime.UtcNow;
                subscription.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Webhook delivered successfully to {Url} for event {EventType} (attempt {Attempt}/{Max}, {Duration}ms)",
                    subscription.Url, eventType, attempt, maxAttempts, delivery.DurationMs);
                return; // Success, no more retries
            }

            subscription.FailedDeliveries++;
            subscription.LastFailureAt = DateTime.UtcNow;
            subscription.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Webhook delivery failed to {Url} for event {EventType} (attempt {Attempt}/{Max}): {Error}",
                subscription.Url, eventType, attempt, maxAttempts, delivery.Error);

            // Exponential backoff: 1s, 4s, 16s (base 4^n pattern: 4^0=1, 4^1=4, 4^2=16)
            if (attempt < maxAttempts)
            {
                var delaySeconds = (int)Math.Pow(4, attempt - 1);
                _logger.LogDebug("Retrying webhook delivery in {Delay}s", delaySeconds);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<WebhookDelivery> DeliverAsync(
        WebhookSubscription subscription,
        string eventType,
        string payloadJson,
        int attemptNumber,
        CancellationToken ct)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            EventType = eventType,
            Payload = payloadJson,
            AttemptNumber = attemptNumber
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = _httpClientFactory.CreateClient("WebhookDelivery");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(5, subscription.TimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, subscription.ContentType);

            // Add standard webhook headers
            request.Headers.Add("X-Webhook-Event", eventType);
            request.Headers.Add("X-Webhook-Delivery", delivery.Id.ToString());
            request.Headers.Add("X-Webhook-Timestamp", DateTime.UtcNow.ToString("O"));

            // Add HMAC-SHA256 signature if secret is configured
            if (!string.IsNullOrEmpty(subscription.Secret))
            {
                var signature = ComputeHmacSha256(payloadJson, subscription.Secret);
                request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
            }

            // Add custom headers from subscription
            if (!string.IsNullOrEmpty(subscription.Headers))
            {
                try
                {
                    var customHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(subscription.Headers);
                    if (customHeaders != null)
                    {
                        foreach (var header in customHeaders)
                        {
                            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse custom headers for subscription {SubscriptionId}", subscription.Id);
                }
            }

            using var response = await client.SendAsync(request, ct);

            stopwatch.Stop();
            delivery.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            delivery.HttpStatusCode = (int)response.StatusCode;

            // Read response body (truncated to 2000 chars)
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            delivery.ResponseBody = responseBody.Length > 2000
                ? responseBody[..2000]
                : responseBody;

            // Consider 2xx status codes as success
            delivery.Success = response.IsSuccessStatusCode;

            if (!delivery.Success)
            {
                delivery.Error = $"HTTP {delivery.HttpStatusCode}: {response.ReasonPhrase}";
            }
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            delivery.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            delivery.Success = false;
            delivery.Error = $"Request timed out after {subscription.TimeoutSeconds}s";
            delivery.HttpStatusCode = 0;
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            delivery.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            delivery.Success = false;
            delivery.Error = $"Connection error: {ex.Message}";
            delivery.HttpStatusCode = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            delivery.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            delivery.Success = false;
            delivery.Error = $"Unexpected error: {ex.Message}";
            delivery.HttpStatusCode = 0;

            _logger.LogError(ex, "Unexpected error delivering webhook to {Url}", subscription.Url);
        }

        // Persist the delivery record
        _context.Set<WebhookDelivery>().Add(delivery);
        await _context.SaveChangesAsync(ct);

        return delivery;
    }

    private static string ComputeHmacSha256(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
