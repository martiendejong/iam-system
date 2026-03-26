using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Workers;

/// <summary>
/// Background job that periodically cleans up expired temporary access grants
/// and old refresh tokens to keep the database lean.
/// Runs every hour.
/// </summary>
public class ExpiredGrantCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExpiredGrantCleanupWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public ExpiredGrantCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<ExpiredGrantCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExpiredGrantCleanupWorker starting, interval: {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown, do not log as error
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during expired grant cleanup");
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

        _logger.LogInformation("ExpiredGrantCleanupWorker stopping");
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var now = DateTime.UtcNow;

        // Mark expired temporary access grants as Expired
        var expiredGrants = await context.TemporaryAccessGrants
            .Where(g => g.EndTime < now && g.Status == TemporaryAccessStatus.Active)
            .ToListAsync(ct);

        foreach (var grant in expiredGrants)
        {
            grant.Status = TemporaryAccessStatus.Expired;
            grant.UpdatedAt = now;
        }

        // Remove refresh tokens that have been expired for more than 30 days
        var cutoff = now.AddDays(-30);
        var expiredTokens = await context.RefreshTokens
            .Where(t => t.ExpiresAt < cutoff)
            .ToListAsync(ct);

        context.RefreshTokens.RemoveRange(expiredTokens);

        var changes = await context.SaveChangesAsync(ct);

        if (expiredGrants.Count > 0 || expiredTokens.Count > 0)
        {
            _logger.LogInformation(
                "Cleanup complete: {GrantCount} grants marked expired, {TokenCount} old refresh tokens removed",
                expiredGrants.Count, expiredTokens.Count);
        }
    }
}
