using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Workers;

/// <summary>
/// Background job that monitors device certificates approaching expiration.
/// Logs warnings for certificates expiring within 30 days and critical warnings
/// for those expiring within 7 days. Groups results by device for summary logging.
/// Runs every 6 hours.
/// </summary>
public class CertificateExpiryMonitorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CertificateExpiryMonitorWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6);

    public CertificateExpiryMonitorWorker(IServiceScopeFactory scopeFactory, ILogger<CertificateExpiryMonitorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CertificateExpiryMonitorWorker starting, interval: {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckExpiringCertificatesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during certificate expiry monitoring");
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

        _logger.LogInformation("CertificateExpiryMonitorWorker stopping");
    }

    private async Task CheckExpiringCertificatesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var now = DateTime.UtcNow;
        var thirtyDaysFromNow = now.AddDays(30);
        var sevenDaysFromNow = now.AddDays(7);

        // Find active certificates expiring within the next 30 days
        var expiringCerts = await context.DeviceCertificates
            .Where(c => c.Status == "Active" && c.NotAfter <= thirtyDaysFromNow && c.NotAfter > now)
            .Select(c => new
            {
                c.Id,
                c.DeviceId,
                DeviceName = c.Device != null ? c.Device.Name : "Unknown",
                DeviceIdentifier = c.Device != null ? c.Device.DeviceId : "Unknown",
                c.SubjectName,
                c.SerialNumber,
                c.NotAfter
            })
            .ToListAsync(ct);

        if (expiringCerts.Count == 0)
        {
            _logger.LogInformation("Certificate expiry check complete: no certificates expiring within 30 days");
            return;
        }

        // Group by device for summary logging
        var byDevice = expiringCerts.GroupBy(c => new { c.DeviceId, c.DeviceName, c.DeviceIdentifier });

        foreach (var deviceGroup in byDevice)
        {
            var criticalCerts = deviceGroup.Where(c => c.NotAfter <= sevenDaysFromNow).ToList();
            var warningCerts = deviceGroup.Where(c => c.NotAfter > sevenDaysFromNow).ToList();

            if (criticalCerts.Count > 0)
            {
                foreach (var cert in criticalCerts)
                {
                    var daysLeft = (cert.NotAfter - now).TotalDays;
                    _logger.LogCritical(
                        "CRITICAL: Certificate {SerialNumber} for device {DeviceName} ({DeviceId}) expires in {DaysLeft:F1} days (at {ExpiresAt:u})",
                        cert.SerialNumber, deviceGroup.Key.DeviceName, deviceGroup.Key.DeviceIdentifier,
                        daysLeft, cert.NotAfter);
                }
            }

            if (warningCerts.Count > 0)
            {
                foreach (var cert in warningCerts)
                {
                    var daysLeft = (cert.NotAfter - now).TotalDays;
                    _logger.LogWarning(
                        "Certificate {SerialNumber} for device {DeviceName} ({DeviceId}) expires in {DaysLeft:F1} days (at {ExpiresAt:u})",
                        cert.SerialNumber, deviceGroup.Key.DeviceName, deviceGroup.Key.DeviceIdentifier,
                        daysLeft, cert.NotAfter);
                }
            }
        }

        var totalCritical = expiringCerts.Count(c => c.NotAfter <= sevenDaysFromNow);
        var totalWarning = expiringCerts.Count - totalCritical;
        var deviceCount = byDevice.Count();

        _logger.LogInformation(
            "Certificate expiry check complete: {TotalExpiring} certificates expiring across {DeviceCount} devices ({Critical} critical, {Warning} warning)",
            expiringCerts.Count, deviceCount, totalCritical, totalWarning);
    }
}
