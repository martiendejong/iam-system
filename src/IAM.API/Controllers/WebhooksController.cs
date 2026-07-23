using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WebhooksController : ControllerBase
{
    private readonly IWebhookService _webhookService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        IWebhookService webhookService,
        IEventBus eventBus,
        ILogger<WebhooksController> logger)
    {
        _webhookService = webhookService;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>
    /// Create a new webhook subscription
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<WebhookSubscriptionResponse>> CreateSubscription(
        [FromBody] CreateWebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required" });

        if (string.IsNullOrWhiteSpace(request.Url))
            return BadRequest(new { error = "URL is required" });

        if (request.TenantId == Guid.Empty)
            return BadRequest(new { error = "TenantId is required" });

        // Get user ID from claims
        var userIdClaim = User.FindFirst("sub")?.Value;
        Guid? userId = Guid.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

        var subscription = new WebhookSubscription
        {
            Name = request.Name,
            Url = request.Url,
            Secret = request.Secret,
            TenantId = request.TenantId,
            CreatedByUserId = userId,
            Events = JsonSerializer.Serialize(request.Events ?? new List<string>()),
            Headers = request.Headers != null ? JsonSerializer.Serialize(request.Headers) : null,
            IsActive = request.IsActive ?? true,
            ContentType = request.ContentType ?? "application/json",
            MaxRetries = request.MaxRetries ?? 3,
            TimeoutSeconds = request.TimeoutSeconds ?? 30
        };

        try
        {
            var created = await _webhookService.CreateSubscriptionAsync(subscription, cancellationToken);
            return CreatedAtAction(
                nameof(GetSubscription),
                new { id = created.Id },
                MapToResponse(created));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List webhook subscriptions for a tenant
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<WebhookSubscriptionResponse>>> GetSubscriptions(
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "TenantId query parameter is required" });

        var subscriptions = await _webhookService.GetSubscriptionsAsync(tenantId, cancellationToken);

        return Ok(subscriptions.Select(MapToResponse).ToList());
    }

    /// <summary>
    /// Get a specific webhook subscription
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WebhookSubscriptionResponse>> GetSubscription(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var subscription = await _webhookService.GetSubscriptionAsync(id, cancellationToken);
        if (subscription == null)
            return NotFound(new { error = "Webhook subscription not found" });

        return Ok(MapToResponse(subscription));
    }

    /// <summary>
    /// Update a webhook subscription
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WebhookSubscriptionResponse>> UpdateSubscription(
        Guid id,
        [FromBody] UpdateWebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await _webhookService.UpdateSubscriptionAsync(
                id,
                request.Name,
                request.Url,
                request.Events,
                request.IsActive,
                cancellationToken);

            return Ok(MapToResponse(updated));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Webhook subscription not found" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a webhook subscription
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteSubscription(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _webhookService.DeleteSubscriptionAsync(id, cancellationToken);
        if (!deleted)
            return NotFound(new { error = "Webhook subscription not found" });

        return Ok(new { message = "Webhook subscription deleted" });
    }

    /// <summary>
    /// Send a test event to a webhook subscription
    /// </summary>
    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<WebhookDeliveryResponse>> TestWebhook(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var delivery = await _webhookService.TestWebhookAsync(id, cancellationToken);

            return Ok(new WebhookDeliveryResponse
            {
                Id = delivery.Id,
                SubscriptionId = delivery.SubscriptionId,
                EventType = delivery.EventType,
                HttpStatusCode = delivery.HttpStatusCode,
                ResponseBody = delivery.ResponseBody,
                AttemptNumber = delivery.AttemptNumber,
                DurationMs = delivery.DurationMs,
                Success = delivery.Success,
                Error = delivery.Error,
                CreatedAt = delivery.CreatedAt
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Webhook subscription not found" });
        }
    }

    /// <summary>
    /// Get delivery history for a webhook subscription
    /// </summary>
    [HttpGet("{id:guid}/deliveries")]
    public async Task<ActionResult<List<WebhookDeliveryResponse>>> GetDeliveryHistory(
        Guid id,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        // Verify subscription exists
        var subscription = await _webhookService.GetSubscriptionAsync(id, cancellationToken);
        if (subscription == null)
            return NotFound(new { error = "Webhook subscription not found" });

        var deliveries = await _eventBus.GetDeliveryHistoryAsync(id, limit, cancellationToken);

        return Ok(deliveries.Select(d => new WebhookDeliveryResponse
        {
            Id = d.Id,
            SubscriptionId = d.SubscriptionId,
            EventType = d.EventType,
            HttpStatusCode = d.HttpStatusCode,
            ResponseBody = d.ResponseBody,
            AttemptNumber = d.AttemptNumber,
            DurationMs = d.DurationMs,
            Success = d.Success,
            Error = d.Error,
            CreatedAt = d.CreatedAt
        }).ToList());
    }

    /// <summary>
    /// List all available event types that can be subscribed to
    /// </summary>
    [HttpGet("event-types")]
    public ActionResult<EventTypesResponse> GetEventTypes()
    {
        var eventTypes = IamEventTypes.All.Select(e => new EventTypeInfo
        {
            Name = e,
            Category = e.Split('.')[0]
        }).ToList();

        return Ok(new EventTypesResponse
        {
            EventTypes = eventTypes,
            WildcardSupported = true,
            WildcardExamples = new List<string> { "*", "user.*", "device.*", "security.*" }
        });
    }

    private static WebhookSubscriptionResponse MapToResponse(WebhookSubscription subscription)
    {
        List<string>? events = null;
        try
        {
            events = JsonSerializer.Deserialize<List<string>>(subscription.Events);
        }
        catch (JsonException) { }

        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrEmpty(subscription.Headers))
        {
            try
            {
                headers = JsonSerializer.Deserialize<Dictionary<string, string>>(subscription.Headers);
            }
            catch (JsonException) { }
        }

        return new WebhookSubscriptionResponse
        {
            Id = subscription.Id,
            Name = subscription.Name,
            Url = subscription.Url,
            HasSecret = !string.IsNullOrEmpty(subscription.Secret),
            TenantId = subscription.TenantId,
            TenantName = subscription.Tenant?.Name,
            CreatedByUserId = subscription.CreatedByUserId,
            Events = events ?? new List<string>(),
            Headers = headers,
            IsActive = subscription.IsActive,
            ContentType = subscription.ContentType,
            MaxRetries = subscription.MaxRetries,
            TimeoutSeconds = subscription.TimeoutSeconds,
            CreatedAt = subscription.CreatedAt,
            UpdatedAt = subscription.UpdatedAt,
            TotalDeliveries = subscription.TotalDeliveries,
            SuccessfulDeliveries = subscription.SuccessfulDeliveries,
            FailedDeliveries = subscription.FailedDeliveries,
            LastDeliveryAt = subscription.LastDeliveryAt,
            LastSuccessAt = subscription.LastSuccessAt,
            LastFailureAt = subscription.LastFailureAt
        };
    }
}

// --- Request DTOs ---

public class CreateWebhookRequest
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Secret { get; set; }
    public Guid TenantId { get; set; }
    public List<string>? Events { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public bool? IsActive { get; set; }
    public string? ContentType { get; set; }
    public int? MaxRetries { get; set; }
    public int? TimeoutSeconds { get; set; }
}

public class UpdateWebhookRequest
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public List<string>? Events { get; set; }
    public bool? IsActive { get; set; }
}

// --- Response DTOs ---

public class WebhookSubscriptionResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool HasSecret { get; set; }
    public Guid TenantId { get; set; }
    public string? TenantName { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public List<string> Events { get; set; } = new();
    public Dictionary<string, string>? Headers { get; set; }
    public bool IsActive { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public int MaxRetries { get; set; }
    public int TimeoutSeconds { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int TotalDeliveries { get; set; }
    public int SuccessfulDeliveries { get; set; }
    public int FailedDeliveries { get; set; }
    public DateTime? LastDeliveryAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
}

public class WebhookDeliveryResponse
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int HttpStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public int AttemptNumber { get; set; }
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class EventTypesResponse
{
    public List<EventTypeInfo> EventTypes { get; set; } = new();
    public bool WildcardSupported { get; set; }
    public List<string> WildcardExamples { get; set; } = new();
}

public class EventTypeInfo
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}
