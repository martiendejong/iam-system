using IAM.Core.Services;

namespace IAM.API.Workers;

/// <summary>
/// Background service that periodically checks for and expires overdue access requests.
/// Runs every 15 minutes.
/// </summary>
public class AccessRequestExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccessRequestExpiryWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(15);

    public AccessRequestExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<AccessRequestExpiryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AccessRequestExpiryWorker starting, interval: {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IAccessRequestService>();

                var expiredCount = await service.ExpireOverdueRequestsAsync(stoppingToken);

                if (expiredCount > 0)
                {
                    _logger.LogInformation("AccessRequestExpiryWorker expired {Count} requests", expiredCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during access request expiry check");
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

        _logger.LogInformation("AccessRequestExpiryWorker stopping");
    }
}
