using System.Security.Cryptography;
using System.Text.Json;
using IAM.Core.Services;
using IAM.Infrastructure.Services;

namespace IAM.API.Workers;

/// <summary>
/// Background service that monitors secrets with scheduled rotation.
/// Checks every 5 minutes for secrets where NextRotationAt has passed.
/// For secrets with AutoGenerate enabled, automatically generates and rotates the value.
/// For manual secrets, logs a warning that rotation is overdue.
/// </summary>
public class SecretRotationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecretRotationWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);

    public SecretRotationWorker(IServiceScopeFactory scopeFactory, ILogger<SecretRotationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SecretRotationWorker starting, check interval: {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRotateSecretsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during secret rotation check");
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

        _logger.LogInformation("SecretRotationWorker stopping");
    }

    private async Task CheckAndRotateSecretsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var secretsService = scope.ServiceProvider.GetRequiredService<ISecretsVaultService>();

        var dueSecrets = await secretsService.GetSecretsDueForRotationAsync(ct);

        if (dueSecrets.Count == 0)
            return;

        _logger.LogInformation("Found {Count} secrets due for rotation", dueSecrets.Count);

        foreach (var secret in dueSecrets)
        {
            try
            {
                // Parse rotation schedule to check if auto-generate is enabled
                RotationScheduleConfig? schedule = null;
                if (!string.IsNullOrEmpty(secret.RotationSchedule))
                {
                    schedule = JsonSerializer.Deserialize<RotationScheduleConfig>(
                        secret.RotationSchedule,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }

                if (schedule?.AutoGenerate == true)
                {
                    // Auto-generate a new secret value
                    var newValue = GenerateSecureRandomString(schedule.AutoGenerateLength > 0 ? schedule.AutoGenerateLength : 64);
                    var gracePeriod = schedule.GracePeriodHours > 0
                        ? TimeSpan.FromHours(schedule.GracePeriodHours)
                        : (TimeSpan?)null;

                    await secretsService.RotateSecretAsync(
                        secret.Id,
                        newPlainTextValue: newValue,
                        rotationReason: "Scheduled",
                        rotatedByUserId: null,
                        gracePeriod: gracePeriod,
                        ct: ct
                    );

                    _logger.LogInformation(
                        "Auto-rotated secret {SecretId} ({SecretName}) to version {Version}, gracePeriod={GracePeriodHours}h",
                        secret.Id, secret.Name, secret.Version + 1, schedule.GracePeriodHours);
                }
                else
                {
                    // Manual rotation required - log warning
                    var overdueDays = (DateTime.UtcNow - secret.NextRotationAt!.Value).TotalDays;
                    _logger.LogWarning(
                        "Secret {SecretId} ({SecretName}) is overdue for rotation by {OverdueDays:F1} days. Manual rotation required (AutoGenerate=false).",
                        secret.Id, secret.Name, overdueDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process rotation for secret {SecretId} ({SecretName})",
                    secret.Id, secret.Name);
            }
        }
    }

    /// <summary>
    /// Generates a cryptographically secure random string using Base64 encoding.
    /// </summary>
    private static string GenerateSecureRandomString(int length)
    {
        var bytes = new byte[(int)Math.Ceiling(length * 0.75)];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)[..length];
    }
}
