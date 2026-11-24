using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

/// <summary>
/// Registers a browser or installed app for Web Push, so a pending sign-in shows up as a notification on that device.
/// The public VAPID key is not secret; the private key stays in server configuration (WebPush:PrivateKey).
/// </summary>
[ApiController]
[Route("api/push")]
public class PushSubscriptionsController : ControllerBase
{
    private const int MaxSubscriptionsPerUser = 10;

    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;

    public PushSubscriptionsController(IAMDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    [HttpGet("vapid-public-key")]
    [AllowAnonymous]
    public IActionResult PublicKey()
    {
        var key = _configuration["WebPush:PublicKey"];
        return string.IsNullOrWhiteSpace(key)
            ? NotFound(new { error = "Push notifications are not configured" })
            : Ok(new { publicKey = key });
    }

    [HttpPost("subscriptions")]
    [Authorize]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Endpoint) || string.IsNullOrWhiteSpace(request.P256dh) || string.IsNullOrWhiteSpace(request.Auth)
            || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return BadRequest(new { error = "A valid https push subscription is required" });
        }

        var existing = await _context.WebPushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);
        if (existing != null)
        {
            // The same browser registering again (maybe after another user signed in on it): it belongs to the caller now.
            existing.UserId = userId.Value;
            existing.P256dh = request.P256dh;
            existing.Auth = request.Auth;
            existing.Label = Truncate(request.Label, 100) ?? existing.Label;
            existing.FailureCount = 0;
        }
        else
        {
            var count = await _context.WebPushSubscriptions.CountAsync(s => s.UserId == userId.Value, ct);
            if (count >= MaxSubscriptionsPerUser)
            {
                var oldest = await _context.WebPushSubscriptions.Where(s => s.UserId == userId.Value).OrderBy(s => s.CreatedAt).FirstAsync(ct);
                _context.WebPushSubscriptions.Remove(oldest);
            }

            _context.WebPushSubscriptions.Add(new WebPushSubscription
            {
                UserId = userId.Value,
                Endpoint = request.Endpoint,
                P256dh = request.P256dh,
                Auth = request.Auth,
                Label = Truncate(request.Label, 100)
            });
        }

        await _context.SaveChangesAsync(ct);
        return Ok(new { subscribed = true });
    }

    [HttpPost("subscriptions/remove")]
    [Authorize]
    public async Task<IActionResult> Unsubscribe([FromBody] PushUnsubscribeRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var existing = await _context.WebPushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint && s.UserId == userId.Value, ct);
        if (existing != null)
        {
            _context.WebPushSubscriptions.Remove(existing);
            await _context.SaveChangesAsync(ct);
        }

        return Ok(new { subscribed = false });
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}

public record PushSubscriptionRequest(string Endpoint, string P256dh, string Auth, string? Label);
public record PushUnsubscribeRequest(string Endpoint);
