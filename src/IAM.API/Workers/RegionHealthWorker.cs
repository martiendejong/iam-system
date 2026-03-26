using IAM.Core.Services;

namespace IAM.API.Workers;

/// <summary>
/// Background service that periodically checks health of all registered regions.
/// Runs every 30 seconds, with an initial 20-second delay to let the app start up.
/// </summary>
public class RegionHealthWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RegionHealthWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    public RegionHealthWorker(IServiceScopeFactory scopeFactory, ILogger<RegionHealthWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RegionHealthWorker starting, check interval: {Interval}", _interval);

        // Initial delay to let the app start up before first health check
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var regionService = scope.ServiceProvider.GetRequiredService<IRegionService>();

                await regionService.RunAllHealthChecksAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during region health check cycle");
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

        _logger.LogInformation("RegionHealthWorker stopping");
    }
}
