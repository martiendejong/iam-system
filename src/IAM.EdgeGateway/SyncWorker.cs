namespace IAM.EdgeGateway;

/// <summary>
/// Background worker that keeps the local authorization cache synchronized with the cloud IAM.
/// - Every configured interval (default 5 minutes): Sync all known device claims from cloud IAM.
/// - Every configured interval (default 60 seconds): Health check to cloud IAM.
/// - On connectivity loss: Log warning and continue serving cached data.
/// - On connectivity restore: Trigger a full re-sync of all tracked devices.
/// </summary>
public class SyncWorker : BackgroundService
{
    private readonly EdgeGatewayService _gateway;
    private readonly EdgeAuthorizationCache _cache;
    private readonly EdgeGatewayOptions _options;
    private readonly ILogger<SyncWorker> _logger;

    public SyncWorker(
        EdgeGatewayService gateway,
        EdgeAuthorizationCache cache,
        EdgeGatewayOptions options,
        ILogger<SyncWorker> logger)
    {
        _gateway = gateway;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "SyncWorker starting. Health check interval: {HealthCheckSeconds}s, Sync interval: {SyncMinutes}min",
            _options.HealthCheckIntervalSeconds, _options.SyncIntervalMinutes);

        // Authenticate the gateway on startup
        await AuthenticateWithRetryAsync(stoppingToken);

        // Run initial full sync
        await FullSyncAsync(stoppingToken);

        // Schedule periodic tasks using two independent timers
        var healthCheckInterval = TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds);
        var syncInterval = TimeSpan.FromMinutes(_options.SyncIntervalMinutes);
        var cleanupInterval = TimeSpan.FromMinutes(1); // Clean expired cache entries every minute

        var lastHealthCheck = DateTime.UtcNow;
        var lastSync = DateTime.UtcNow;
        var lastCleanup = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Determine the next event
                var nextHealthCheck = lastHealthCheck + healthCheckInterval;
                var nextSync = lastSync + syncInterval;
                var nextCleanup = lastCleanup + cleanupInterval;
                var nextEvent = Min(nextHealthCheck, Min(nextSync, nextCleanup));
                var delay = nextEvent - DateTime.UtcNow;

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken);
                }

                var now = DateTime.UtcNow;

                // Health check
                if (now >= nextHealthCheck)
                {
                    lastHealthCheck = now;
                    var connectivityRestored = await _gateway.HealthCheckAsync(stoppingToken);

                    if (connectivityRestored)
                    {
                        // Connectivity was just restored -- re-authenticate and do a full sync
                        _logger.LogInformation("Connectivity restored. Re-authenticating and performing full sync");
                        await AuthenticateWithRetryAsync(stoppingToken);
                        await FullSyncAsync(stoppingToken);
                        lastSync = DateTime.UtcNow;
                    }
                }

                // Periodic sync
                if (now >= nextSync)
                {
                    lastSync = now;
                    if (_gateway.IsOnline)
                    {
                        await FullSyncAsync(stoppingToken);
                    }
                    else
                    {
                        _logger.LogDebug("Skipping sync -- gateway is offline");
                    }
                }

                // Cache cleanup
                if (now >= nextCleanup)
                {
                    lastCleanup = now;
                    _cache.CleanExpired();
                    LogCacheStats();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in SyncWorker loop. Continuing after 10s delay");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("SyncWorker stopped");
    }

    /// <summary>
    /// Authenticate the gateway with the cloud IAM, retrying with exponential backoff.
    /// </summary>
    private async Task AuthenticateWithRetryAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        var maxRetryDelay = TimeSpan.FromMinutes(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            var success = await _gateway.AuthenticateGatewayAsync(stoppingToken);
            if (success)
            {
                return;
            }

            _logger.LogWarning(
                "Gateway authentication failed. Retrying in {RetrySeconds}s. Operating in offline mode",
                retryDelay.TotalSeconds);

            try
            {
                await Task.Delay(retryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Exponential backoff with cap
            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
        }
    }

    /// <summary>
    /// Sync claims for all tracked devices. Invalidates the entire cache first if needed.
    /// </summary>
    private async Task FullSyncAsync(CancellationToken stoppingToken)
    {
        var trackedDevices = _cache.GetTrackedDeviceIds();

        if (trackedDevices.Count == 0)
        {
            _logger.LogDebug("No tracked devices to sync");
            return;
        }

        _logger.LogInformation("Starting full sync for {DeviceCount} tracked devices", trackedDevices.Count);

        var syncedCount = 0;
        var failedCount = 0;

        foreach (var deviceId in trackedDevices)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await _gateway.SyncDeviceClaimsAsync(deviceId, stoppingToken);
                syncedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync claims for device {DeviceId}", deviceId);
                failedCount++;
            }
        }

        _logger.LogInformation(
            "Full sync completed: {Synced} synced, {Failed} failed out of {Total} devices",
            syncedCount, failedCount, trackedDevices.Count);
    }

    private void LogCacheStats()
    {
        var stats = _cache.GetStats();
        _logger.LogDebug(
            "Cache stats: {Devices} devices, {Decisions} decisions, hit rate {HitRate:P1} ({Hits} hits, {Misses} misses)",
            stats.CachedDevices, stats.CachedDecisions, stats.HitRate, stats.Hits, stats.Misses);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
