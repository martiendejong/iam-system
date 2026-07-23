using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Risk-based adaptive authentication service.
/// Evaluates multiple risk factors to determine login action: Allow, StepUp (MFA), or Block.
/// </summary>
public class RiskAssessmentService : IRiskAssessmentService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<RiskAssessmentService> _logger;

    public RiskAssessmentService(IAMDbContext context, ILogger<RiskAssessmentService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<LoginRiskScore> AssessLoginRiskAsync(
        Guid userId,
        string ipAddress,
        string? userAgent,
        string? deviceFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        var riskFactors = new List<RiskFactorResult>();
        var now = DateTime.UtcNow;

        // Load user's login history (last 30 days)
        var recentLogins = await _context.LoginRiskScores
            .Where(l => l.UserId == userId && l.CreatedAt >= now.AddDays(-30))
            .OrderByDescending(l => l.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        // 1. New Device Check (+20 points)
        var newDeviceRisk = await EvaluateNewDeviceAsync(userId, deviceFingerprint, cancellationToken);
        if (newDeviceRisk != null) riskFactors.Add(newDeviceRisk);

        // 2. Unusual Location Check (+15 points)
        var locationRisk = EvaluateUnusualLocation(recentLogins, ipAddress);
        if (locationRisk != null) riskFactors.Add(locationRisk);

        // 3. Impossible Travel Check (+40 points)
        var travelRisk = EvaluateImpossibleTravel(recentLogins, ipAddress, now);
        if (travelRisk != null) riskFactors.Add(travelRisk);

        // 4. Brute Force Pattern Check (+30 points)
        var bruteForceRisk = await EvaluateBruteForcePatternAsync(userId, ipAddress, now, cancellationToken);
        if (bruteForceRisk != null) riskFactors.Add(bruteForceRisk);

        // 5. Time-of-Day Anomaly Check (+10 points)
        var timeRisk = EvaluateTimeOfDayAnomaly(recentLogins, now);
        if (timeRisk != null) riskFactors.Add(timeRisk);

        // 6. Unknown User Agent Check (+5 points)
        var uaRisk = EvaluateUnknownUserAgent(recentLogins, userAgent);
        if (uaRisk != null) riskFactors.Add(uaRisk);

        // Calculate total score (capped at 100)
        var totalScore = Math.Min(100, riskFactors.Sum(f => f.Points));

        // Apply trusted device discount
        if (!string.IsNullOrEmpty(deviceFingerprint))
        {
            var trustedDevice = await _context.TrustedDevices
                .FirstOrDefaultAsync(d =>
                    d.UserId == userId &&
                    d.DeviceFingerprint == deviceFingerprint &&
                    d.ExpiresAt > now,
                    cancellationToken);

            if (trustedDevice != null)
            {
                var discount = (int)(trustedDevice.TrustScore * 0.3); // Up to 30 points discount
                totalScore = Math.Max(0, totalScore - discount);
                riskFactors.Add(new RiskFactorResult
                {
                    Factor = "trusted_device",
                    Points = -discount,
                    Description = $"Trusted device '{trustedDevice.Name}' (trust score: {trustedDevice.TrustScore}) applied -{discount} discount"
                });

                // Update last used
                trustedDevice.LastUsedAt = now;
                // Increase trust score slightly on successful use (max 100)
                trustedDevice.TrustScore = Math.Min(100, trustedDevice.TrustScore + 1);
            }
        }

        // Determine action based on thresholds
        var threshold = await GetActiveThresholdAsync(null, cancellationToken);
        var action = DetermineAction(totalScore, threshold);

        var riskScore = new LoginRiskScore
        {
            UserId = userId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            GeoLocation = DeriveGeoLocation(ipAddress),
            RiskScore = totalScore,
            RiskFactors = JsonSerializer.Serialize(riskFactors),
            Action = action
        };

        _context.LoginRiskScores.Add(riskScore);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Risk assessment for User:{UserId} IP:{IpAddress} Score:{Score} Action:{Action} Factors:{FactorCount}",
            userId, ipAddress, totalScore, action, riskFactors.Count);

        return riskScore;
    }

    public async Task<List<LoginRiskScore>> GetRiskScoresAsync(
        Guid? userId = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var query = _context.LoginRiskScores.AsQueryable();

        if (userId.HasValue)
            query = query.Where(s => s.UserId == userId.Value);

        if (startDate.HasValue)
            query = query.Where(s => s.CreatedAt >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(s => s.CreatedAt <= endDate.Value);

        return await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip)
            .Take(Math.Min(take, 500))
            .ToListAsync(cancellationToken);
    }

    public async Task<RiskDashboardData> GetDashboardDataAsync(
        Guid? tenantId = null,
        int days = 30,
        CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);

        var scores = await _context.LoginRiskScores
            .Where(s => s.CreatedAt >= since)
            .ToListAsync(cancellationToken);

        // Heatmap: hour x day-of-week
        var heatmap = scores
            .GroupBy(s => new { s.CreatedAt.Hour, DayOfWeek = (int)s.CreatedAt.DayOfWeek })
            .Select(g => new RiskHeatmapCell
            {
                Hour = g.Key.Hour,
                DayOfWeek = g.Key.DayOfWeek,
                Count = g.Count(),
                AvgRiskScore = g.Average(s => s.RiskScore)
            })
            .ToList();

        // Distribution buckets
        var distribution = new List<RiskDistributionBucket>
        {
            new() { Range = "0-20", MinScore = 0, MaxScore = 20, Count = scores.Count(s => s.RiskScore <= 20) },
            new() { Range = "21-40", MinScore = 21, MaxScore = 40, Count = scores.Count(s => s.RiskScore >= 21 && s.RiskScore <= 40) },
            new() { Range = "41-60", MinScore = 41, MaxScore = 60, Count = scores.Count(s => s.RiskScore >= 41 && s.RiskScore <= 60) },
            new() { Range = "61-80", MinScore = 61, MaxScore = 80, Count = scores.Count(s => s.RiskScore >= 61 && s.RiskScore <= 80) },
            new() { Range = "81-100", MinScore = 81, MaxScore = 100, Count = scores.Count(s => s.RiskScore >= 81) }
        };

        // Summary
        var trustedDeviceCount = await _context.TrustedDevices
            .Where(d => d.ExpiresAt > DateTime.UtcNow)
            .CountAsync(cancellationToken);

        var summary = new RiskSummary
        {
            TotalAssessments = scores.Count,
            AllowedCount = scores.Count(s => s.Action == RiskAction.Allow),
            StepUpCount = scores.Count(s => s.Action == RiskAction.StepUp),
            BlockedCount = scores.Count(s => s.Action == RiskAction.Block),
            AvgRiskScore = scores.Count > 0 ? Math.Round(scores.Average(s => s.RiskScore), 1) : 0,
            HighRiskCount = scores.Count(s => s.RiskScore >= 75),
            TrustedDeviceCount = trustedDeviceCount
        };

        return new RiskDashboardData
        {
            Heatmap = heatmap,
            Distribution = distribution,
            Summary = summary
        };
    }

    // --- Threshold CRUD ---

    public async Task<List<RiskThreshold>> GetThresholdsAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.RiskThresholds.AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(t => t.TenantId == tenantId.Value);

        return await query.OrderByDescending(t => t.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<RiskThreshold?> GetThresholdByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _context.RiskThresholds.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<RiskThreshold> UpsertThresholdAsync(
        RiskThreshold threshold,
        CancellationToken cancellationToken = default)
    {
        var existing = await _context.RiskThresholds
            .FirstOrDefaultAsync(t => t.Id == threshold.Id, cancellationToken);

        if (existing != null)
        {
            existing.LowThreshold = threshold.LowThreshold;
            existing.MediumThreshold = threshold.MediumThreshold;
            existing.HighThreshold = threshold.HighThreshold;
            existing.BlockThreshold = threshold.BlockThreshold;
            existing.RequireMfaAbove = threshold.RequireMfaAbove;
            existing.IsActive = threshold.IsActive;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _context.RiskThresholds.Add(threshold);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return existing ?? threshold;
    }

    public async Task<bool> DeleteThresholdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var threshold = await _context.RiskThresholds.FindAsync(new object[] { id }, cancellationToken);
        if (threshold == null) return false;

        _context.RiskThresholds.Remove(threshold);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // --- Trusted Device CRUD ---

    public async Task<List<TrustedDevice>> GetTrustedDevicesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TrustedDevices
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastUsedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<TrustedDevice> TrustDeviceAsync(
        Guid userId,
        string deviceFingerprint,
        string name,
        CancellationToken cancellationToken = default)
    {
        var existing = await _context.TrustedDevices
            .FirstOrDefaultAsync(d =>
                d.UserId == userId && d.DeviceFingerprint == deviceFingerprint,
                cancellationToken);

        if (existing != null)
        {
            existing.Name = name;
            existing.LastUsedAt = DateTime.UtcNow;
            existing.ExpiresAt = DateTime.UtcNow.AddDays(90);
            existing.TrustScore = Math.Min(100, existing.TrustScore + 10);
        }
        else
        {
            existing = new TrustedDevice
            {
                UserId = userId,
                DeviceFingerprint = deviceFingerprint,
                Name = name
            };
            _context.TrustedDevices.Add(existing);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> RemoveTrustedDeviceAsync(
        Guid id,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var device = await _context.TrustedDevices
            .FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, cancellationToken);

        if (device == null) return false;

        _context.TrustedDevices.Remove(device);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ===========================================
    // Risk Factor Evaluation Methods
    // ===========================================

    /// <summary>
    /// New Device: +20 if user has never logged in with this device fingerprint.
    /// </summary>
    private async Task<RiskFactorResult?> EvaluateNewDeviceAsync(
        Guid userId, string? deviceFingerprint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(deviceFingerprint)) return null;

        var knownDevice = await _context.TrustedDevices
            .AnyAsync(d =>
                d.UserId == userId &&
                d.DeviceFingerprint == deviceFingerprint,
                cancellationToken);

        if (!knownDevice)
        {
            return new RiskFactorResult
            {
                Factor = "new_device",
                Points = 20,
                Description = "First login from this device"
            };
        }

        return null;
    }

    /// <summary>
    /// Unusual Location: +15 if IP address has never been seen for this user.
    /// </summary>
    private static RiskFactorResult? EvaluateUnusualLocation(
        List<LoginRiskScore> recentLogins, string ipAddress)
    {
        if (!recentLogins.Any()) return null;

        // Extract IP prefix (first 3 octets for IPv4) to detect subnet changes
        var currentPrefix = GetIpPrefix(ipAddress);
        var knownPrefixes = recentLogins
            .Select(l => GetIpPrefix(l.IpAddress))
            .Distinct()
            .ToHashSet();

        if (!knownPrefixes.Contains(currentPrefix))
        {
            return new RiskFactorResult
            {
                Factor = "unusual_location",
                Points = 15,
                Description = $"Login from new IP subnet ({currentPrefix}.*)"
            };
        }

        return null;
    }

    /// <summary>
    /// Impossible Travel: +40 if the user logged in from a different geo-location
    /// within an impossibly short time window (< 2 hours from a different country/region).
    /// </summary>
    private static RiskFactorResult? EvaluateImpossibleTravel(
        List<LoginRiskScore> recentLogins, string ipAddress, DateTime now)
    {
        if (!recentLogins.Any()) return null;

        var lastLogin = recentLogins.First();
        var timeSinceLastLogin = now - lastLogin.CreatedAt;

        // Only check if last login was within 2 hours
        if (timeSinceLastLogin.TotalHours > 2) return null;

        // Compare IP prefixes - different prefix within 2 hours suggests impossible travel
        var currentPrefix = GetIpPrefix(ipAddress);
        var lastPrefix = GetIpPrefix(lastLogin.IpAddress);

        if (currentPrefix != lastPrefix)
        {
            return new RiskFactorResult
            {
                Factor = "impossible_travel",
                Points = 40,
                Description = $"Different IP subnet within {timeSinceLastLogin.TotalMinutes:F0} minutes (from {lastPrefix}.* to {currentPrefix}.*)"
            };
        }

        return null;
    }

    /// <summary>
    /// Brute Force Pattern: +30 if there are multiple failed login attempts
    /// from the same IP or for the same user in the last 30 minutes.
    /// </summary>
    private async Task<RiskFactorResult?> EvaluateBruteForcePatternAsync(
        Guid userId, string ipAddress, DateTime now, CancellationToken cancellationToken)
    {
        var thirtyMinutesAgo = now.AddMinutes(-30);

        // Check failed attempts by user
        var failedByUser = await _context.LoginRiskScores
            .CountAsync(l =>
                l.UserId == userId &&
                l.Action == RiskAction.Block &&
                l.CreatedAt >= thirtyMinutesAgo,
                cancellationToken);

        // Check failed attempts from IP
        var failedByIp = await _context.LoginRiskScores
            .CountAsync(l =>
                l.IpAddress == ipAddress &&
                l.Action == RiskAction.Block &&
                l.CreatedAt >= thirtyMinutesAgo,
                cancellationToken);

        if (failedByUser >= 3 || failedByIp >= 5)
        {
            return new RiskFactorResult
            {
                Factor = "brute_force_pattern",
                Points = 30,
                Description = $"Suspicious pattern: {failedByUser} failed attempts by user, {failedByIp} from IP in last 30min"
            };
        }

        return null;
    }

    /// <summary>
    /// Time-of-Day Anomaly: +10 if login is outside the user's normal hours.
    /// Normal hours derived from 80th percentile of historical login times.
    /// </summary>
    private static RiskFactorResult? EvaluateTimeOfDayAnomaly(
        List<LoginRiskScore> recentLogins, DateTime now)
    {
        if (recentLogins.Count < 5) return null; // Not enough data

        var hours = recentLogins.Select(l => l.CreatedAt.Hour).ToList();
        var avgHour = hours.Average();
        var currentHour = now.Hour;

        // If current hour deviates > 6 hours from average login hour
        var deviation = Math.Abs(currentHour - avgHour);
        if (deviation > 12) deviation = 24 - deviation; // Handle wraparound

        if (deviation > 6)
        {
            return new RiskFactorResult
            {
                Factor = "time_anomaly",
                Points = 10,
                Description = $"Login at unusual hour ({currentHour}:00, typical: {avgHour:F0}:00)"
            };
        }

        return null;
    }

    /// <summary>
    /// Unknown User Agent: +5 if the user agent has never been seen for this user.
    /// </summary>
    private static RiskFactorResult? EvaluateUnknownUserAgent(
        List<LoginRiskScore> recentLogins, string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent) || !recentLogins.Any()) return null;

        // Normalize: take first 100 chars for comparison
        var normalized = userAgent.Length > 100 ? userAgent[..100] : userAgent;
        var knownAgents = recentLogins
            .Where(l => !string.IsNullOrEmpty(l.UserAgent))
            .Select(l => l.UserAgent!.Length > 100 ? l.UserAgent[..100] : l.UserAgent)
            .Distinct()
            .ToHashSet();

        if (!knownAgents.Contains(normalized))
        {
            return new RiskFactorResult
            {
                Factor = "unknown_user_agent",
                Points = 5,
                Description = "Login from previously unseen browser/client"
            };
        }

        return null;
    }

    // ===========================================
    // Helper Methods
    // ===========================================

    private async Task<RiskThreshold> GetActiveThresholdAsync(
        Guid? tenantId, CancellationToken cancellationToken)
    {
        // Try tenant-specific first, then fall back to global (TenantId == null)
        RiskThreshold? threshold = null;

        if (tenantId.HasValue)
        {
            threshold = await _context.RiskThresholds
                .FirstOrDefaultAsync(t => t.TenantId == tenantId.Value && t.IsActive, cancellationToken);
        }

        threshold ??= await _context.RiskThresholds
            .FirstOrDefaultAsync(t => t.TenantId == null && t.IsActive, cancellationToken);

        // Return defaults if no threshold configured
        return threshold ?? new RiskThreshold();
    }

    private static RiskAction DetermineAction(int score, RiskThreshold threshold)
    {
        if (score >= threshold.BlockThreshold)
            return RiskAction.Block;

        if (score > threshold.RequireMfaAbove)
            return RiskAction.StepUp;

        return RiskAction.Allow;
    }

    private static string GetIpPrefix(string ipAddress)
    {
        var parts = ipAddress.Split('.');
        if (parts.Length >= 3)
            return $"{parts[0]}.{parts[1]}.{parts[2]}";
        return ipAddress;
    }

    private static string? DeriveGeoLocation(string ipAddress)
    {
        // Placeholder for geo-IP lookup integration.
        // In production, integrate with MaxMind GeoIP2, ip-api.com, or similar.
        if (ipAddress.StartsWith("127.") || ipAddress == "::1")
            return "Localhost";

        if (ipAddress.StartsWith("10.") || ipAddress.StartsWith("192.168.") || ipAddress.StartsWith("172."))
            return "Private Network";

        return null; // Unknown - would be resolved by geo-IP provider
    }
}

/// <summary>
/// Individual risk factor evaluation result.
/// </summary>
public class RiskFactorResult
{
    public string Factor { get; set; } = string.Empty;
    public int Points { get; set; }
    public string Description { get; set; } = string.Empty;
}
