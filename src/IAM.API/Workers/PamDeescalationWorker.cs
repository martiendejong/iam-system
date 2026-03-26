using IAM.Core.Services;

namespace IAM.API.Workers;

/// <summary>
/// Background service that periodically checks for expired privileged access sessions
/// and automatically de-escalates them (revokes elevated privileges).
/// Runs every 30 seconds for timely de-escalation.
/// </summary>
public class PamDeescalationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PamDeescalationWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    public PamDeescalationWorker(IServiceScopeFactory scopeFactory, ILogger<PamDeescalationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PamDeescalationWorker starting, interval: {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessExpiredSessionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during PAM de-escalation check");
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

        _logger.LogInformation("PamDeescalationWorker stopping");
    }

    private async Task ProcessExpiredSessionsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var pamService = scope.ServiceProvider.GetRequiredService<IPrivilegedAccessService>();

        var expiredCount = await pamService.ProcessExpiredSessionsAsync(ct);

        if (expiredCount > 0)
        {
            _logger.LogWarning(
                "PAM De-escalation: {Count} privileged sessions auto-expired",
                expiredCount);
        }
    }
}
