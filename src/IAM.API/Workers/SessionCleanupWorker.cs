using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Workers;

/// <summary>
/// Background job that cleans up expired and old user sessions.
/// Auto-expires sessions past their ExpiresAt time and removes
/// sessions older than 90 days to prevent unbounded table growth.
/// Runs every 30 minutes.
/// </summary>
public class SessionCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionCleanupWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(30);
    private readonly TimeSpan _maxSessionAge = TimeSpan.FromDays(90);

    public SessionCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<SessionCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SessionCleanupWorker starting, interval: {Interval}", _interval);

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
                _logger.LogError(ex, "Error during session cleanup");
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

        _logger.LogInformation("SessionCleanupWorker stopping");
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var now = DateTime.UtcNow;

        // Auto-expire sessions that have passed their ExpiresAt but are not yet revoked
        var expiredSessions = await context.UserSessions
            .Where(s => s.ExpiresAt < now && !s.IsRevoked)
            .ToListAsync(ct);

        foreach (var session in expiredSessions)
        {
            session.IsRevoked = true;
            session.RevokedAt = now;
            session.RevokedReason = "expired";
        }

        // Remove sessions older than 90 days (already expired/revoked historical data)
        var ageCutoff = now - _maxSessionAge;
        var oldSessions = await context.UserSessions
            .Where(s => s.CreatedAt < ageCutoff)
            .ToListAsync(ct);

        context.UserSessions.RemoveRange(oldSessions);

        await context.SaveChangesAsync(ct);

        if (expiredSessions.Count > 0 || oldSessions.Count > 0)
        {
            _logger.LogInformation(
                "Session cleanup complete: {Expired} sessions auto-expired, {Removed} old sessions removed (> {MaxAge} days)",
                expiredSessions.Count, oldSessions.Count, _maxSessionAge.TotalDays);
        }
    }
}
