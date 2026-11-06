using IAM.Core.Services;

namespace IAM.API.Workers;

/// <summary>
/// Background job that clears stored GDPR export text (DataRequests.DataUrl) once the
/// export has expired. The first run also clears any pre-existing backlog.
/// Runs every hour.
/// </summary>
public class DataExportCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataExportCleanupWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public DataExportCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<DataExportCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataExportCleanupWorker starting, interval: {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IDataRequestService>();
                await service.PurgeExpiredExportsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during data export cleanup");
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

        _logger.LogInformation("DataExportCleanupWorker stopping");
    }
}
