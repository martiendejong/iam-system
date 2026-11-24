using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebPush;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Sends a Web Push notification to every device a user registered, so the "approve this sign-in?" prompt appears
/// on the phone even when the app is closed. The payload carries only the request id and the device description,
/// never the number to match. Configure with WebPush:PublicKey, WebPush:PrivateKey and WebPush:Subject (a mailto: or
/// https: contact); without keys this does nothing and the popup/QR routes still work.
/// </summary>
public class WebPushLoginApprovalNotifier : ILoginApprovalNotifier
{
    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebPushLoginApprovalNotifier> _logger;

    public WebPushLoginApprovalNotifier(IAMDbContext context, IConfiguration configuration, ILogger<WebPushLoginApprovalNotifier> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task NotifyAsync(Guid userId, LoginApproval approval, CancellationToken ct = default)
    {
        var publicKey = _configuration["WebPush:PublicKey"];
        var privateKey = _configuration["WebPush:PrivateKey"];
        var subject = _configuration["WebPush:Subject"] ?? "mailto:admin@localhost";
        if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
        {
            return;
        }

        try
        {
            var subscriptions = await _context.WebPushSubscriptions.Where(s => s.UserId == userId).ToListAsync(ct);
            if (subscriptions.Count == 0)
            {
                return;
            }

            var payload = JsonSerializer.Serialize(new
            {
                type = "login-approval",
                id = approval.Id,
                device = approval.RequestDevice,
                url = $"approve/{approval.Id}" // relative to the service worker scope (the admin-ui base path)
            });
            var vapid = new VapidDetails(subject, publicKey, privateKey);
            var client = new WebPushClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            foreach (var sub in subscriptions)
            {
                try
                {
                    var subscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                    await client.SendNotificationAsync(subscription, payload, vapid, timeout.Token);
                    sub.LastSuccessAt = DateTime.UtcNow;
                    sub.FailureCount = 0;
                }
                catch (WebPushException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
                {
                    // The browser dropped this subscription (app uninstalled / permission revoked).
                    _context.WebPushSubscriptions.Remove(sub);
                }
                catch (Exception ex)
                {
                    sub.FailureCount++;
                    _logger.LogWarning(ex, "Web push to subscription {SubscriptionId} failed", sub.Id);
                }
            }

            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Push is best effort: the request exists and shows in the popup and via QR.
            _logger.LogWarning(ex, "Could not send login approval push for user {UserId}", userId);
        }
    }
}
