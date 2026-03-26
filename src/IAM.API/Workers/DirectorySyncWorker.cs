using IAM.Core.Services;

namespace IAM.API.Workers;

/// <summary>
/// Background service that periodically checks for directory sync configurations
/// due for automatic synchronization and runs them.
/// Checks every 5 minutes for configs that need syncing.
/// </summary>
public class DirectorySyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DirectorySyncWorker> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);

    public DirectorySyncWorker(IServiceScopeFactory scopeFactory, ILogger<DirectorySyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DirectorySyncWorker starting, check interval: {Interval}", _checkInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueSyncsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during directory sync worker cycle");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("DirectorySyncWorker stopping");
    }

    private async Task ProcessDueSyncsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IDirectorySyncService>();

        var dueConfigs = await syncService.GetConfigsDueForSyncAsync(ct);

        if (dueConfigs.Count == 0) return;

        _logger.LogInformation("Found {Count} directory sync configs due for automatic sync", dueConfigs.Count);

        foreach (var config in dueConfigs)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                _logger.LogInformation("Starting automatic delta sync for config {ConfigId} ({Name})",
                    config.Id, config.Name);

                // Use delta sync for automatic runs (more efficient)
                // Fall back to full sync if this is the first ever sync
                if (config.LastSyncAt.HasValue)
                {
                    await syncService.RunDeltaSyncAsync(config.Id, ct);
                }
                else
                {
                    await syncService.RunFullSyncAsync(config.Id, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic sync failed for config {ConfigId} ({Name})",
                    config.Id, config.Name);
            }
        }
    }
}
