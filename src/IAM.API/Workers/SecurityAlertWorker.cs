using IAM.Core.Services;

namespace IAM.API.Workers;

/// <summary>
/// Background service that periodically evaluates alert conditions
/// (brute force, impossible travel, privilege escalation, mass deletion).
/// Runs every 60 seconds.
/// </summary>
public class SecurityAlertWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecurityAlertWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(60);

    public SecurityAlertWorker(IServiceScopeFactory scopeFactory, ILogger<SecurityAlertWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SecurityAlertWorker starting, evaluation interval: {Interval}", _interval);

        // Initial delay to let the app start up before first evaluation
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
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
                var alertService = scope.ServiceProvider.GetRequiredService<ISecurityAlertService>();

                await alertService.EvaluateAlertConditionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during security alert condition evaluation");
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

        _logger.LogInformation("SecurityAlertWorker stopping");
    }
}
