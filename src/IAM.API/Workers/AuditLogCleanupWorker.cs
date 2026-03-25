using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Workers;

/// <summary>
/// Background job that cleans up old audit data to prevent unbounded database growth.
/// Deletes AuditLog entries, PolicyAuditEvents, and WebhookDelivery records
/// older than their respective retention periods.
/// Runs daily (every 24 hours).
/// </summary>
public class AuditLogCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditLogCleanupWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);

    public AuditLogCleanupWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AuditLogCleanupWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AuditLogCleanupWorker starting, interval: {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during audit log cleanup");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("AuditLogCleanupWorker stopping");
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var now = DateTime.UtcNow;

        // Configurable retention period for audit logs (default: 90 days)
        var retentionDays = _configuration.GetValue<int>("AuditLog:RetentionDays", 90);
        var auditCutoff = now.AddDays(-retentionDays);

        // Webhook delivery retention (fixed at 30 days)
        var webhookCutoff = now.AddDays(-30);

        _logger.LogInformation(
            "Audit cleanup starting: retention={RetentionDays} days (cutoff {AuditCutoff:u}), webhook cutoff {WebhookCutoff:u}",
            retentionDays, auditCutoff, webhookCutoff);

        // Clean old audit log entries
        var oldAuditLogs = await context.AuditLogs
            .Where(a => a.CreatedAt < auditCutoff)
            .ToListAsync(ct);

        context.AuditLogs.RemoveRange(oldAuditLogs);

        // Clean old policy audit events
        var oldPolicyEvents = await context.PolicyAuditEvents
            .Where(e => e.CreatedAt < auditCutoff)
            .ToListAsync(ct);

        context.PolicyAuditEvents.RemoveRange(oldPolicyEvents);

        // Clean old webhook delivery records
        var oldWebhookDeliveries = await context.Set<WebhookDelivery>()
            .Where(w => w.CreatedAt < webhookCutoff)
            .ToListAsync(ct);

        context.Set<WebhookDelivery>().RemoveRange(oldWebhookDeliveries);

        await context.SaveChangesAsync(ct);

        if (oldAuditLogs.Count > 0 || oldPolicyEvents.Count > 0 || oldWebhookDeliveries.Count > 0)
        {
            _logger.LogInformation(
                "Audit cleanup complete: {AuditLogs} audit logs, {PolicyEvents} policy events, {Webhooks} webhook deliveries removed",
                oldAuditLogs.Count, oldPolicyEvents.Count, oldWebhookDeliveries.Count);
        }
        else
        {
            _logger.LogInformation("Audit cleanup complete: no records exceeded retention period");
        }
    }
}
