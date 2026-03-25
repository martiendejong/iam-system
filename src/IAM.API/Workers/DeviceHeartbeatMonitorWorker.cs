using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Workers;

/// <summary>
/// Background job that monitors device heartbeats and marks devices as offline
/// when they have not been seen for more than 10 minutes.
/// Runs every 5 minutes.
/// </summary>
public class DeviceHeartbeatMonitorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceHeartbeatMonitorWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _offlineThreshold = TimeSpan.FromMinutes(10);

    public DeviceHeartbeatMonitorWorker(IServiceScopeFactory scopeFactory, ILogger<DeviceHeartbeatMonitorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeviceHeartbeatMonitorWorker starting, interval: {Interval}, offline threshold: {Threshold}",
            _interval, _offlineThreshold);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckHeartbeatsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during device heartbeat monitoring");
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

        _logger.LogInformation("DeviceHeartbeatMonitorWorker stopping");
    }

    private async Task CheckHeartbeatsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var cutoff = DateTime.UtcNow - _offlineThreshold;

        // Find devices that are marked online but haven't been seen recently
        var staleDevices = await context.Devices
            .Where(d => d.IsOnline && (d.LastSeenAt == null || d.LastSeenAt < cutoff))
            .ToListAsync(ct);

        if (staleDevices.Count == 0)
            return;

        foreach (var device in staleDevices)
        {
            device.IsOnline = false;
            device.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Device heartbeat check: {Count} device(s) marked offline (no heartbeat for > {Threshold} minutes)",
            staleDevices.Count, _offlineThreshold.TotalMinutes);
    }
}
