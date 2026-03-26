using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for risk-based adaptive authentication.
/// Evaluates login risk, manages device trust, and enforces risk thresholds.
/// </summary>
public interface IRiskAssessmentService
{
    /// <summary>
    /// Assess the risk of a login attempt and determine the appropriate action.
    /// </summary>
    Task<LoginRiskScore> AssessLoginRiskAsync(
        Guid userId,
        string ipAddress,
        string? userAgent,
        string? deviceFingerprint = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get risk score history for a user.
    /// </summary>
    Task<List<LoginRiskScore>> GetRiskScoresAsync(
        Guid? userId = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get dashboard data: heatmap (hour-of-day vs day-of-week risk), distribution, and summary stats.
    /// </summary>
    Task<RiskDashboardData> GetDashboardDataAsync(
        Guid? tenantId = null,
        int days = 30,
        CancellationToken cancellationToken = default);

    // --- Risk Threshold CRUD ---

    /// <summary>
    /// Get risk thresholds, optionally filtered by tenant.
    /// </summary>
    Task<List<RiskThreshold>> GetThresholdsAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a single risk threshold by ID.
    /// </summary>
    Task<RiskThreshold?> GetThresholdByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update a risk threshold configuration.
    /// </summary>
    Task<RiskThreshold> UpsertThresholdAsync(
        RiskThreshold threshold,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a risk threshold configuration.
    /// </summary>
    Task<bool> DeleteThresholdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // --- Trusted Device CRUD ---

    /// <summary>
    /// Get trusted devices for a user.
    /// </summary>
    Task<List<TrustedDevice>> GetTrustedDevicesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Register or update a trusted device.
    /// </summary>
    Task<TrustedDevice> TrustDeviceAsync(
        Guid userId,
        string deviceFingerprint,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a trusted device.
    /// </summary>
    Task<bool> RemoveTrustedDeviceAsync(
        Guid id,
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dashboard aggregate data for risk visualization.
/// </summary>
public class RiskDashboardData
{
    /// <summary>
    /// Heatmap: risk counts by hour-of-day (0-23) and day-of-week (0=Sun - 6=Sat).
    /// Each entry: { hour, dayOfWeek, count, avgRiskScore }
    /// </summary>
    public List<RiskHeatmapCell> Heatmap { get; set; } = new();

    /// <summary>
    /// Distribution of risk scores in buckets: 0-20, 21-40, 41-60, 61-80, 81-100
    /// </summary>
    public List<RiskDistributionBucket> Distribution { get; set; } = new();

    /// <summary>
    /// Summary statistics
    /// </summary>
    public RiskSummary Summary { get; set; } = new();
}

public class RiskHeatmapCell
{
    public int Hour { get; set; }
    public int DayOfWeek { get; set; }
    public int Count { get; set; }
    public double AvgRiskScore { get; set; }
}

public class RiskDistributionBucket
{
    public string Range { get; set; } = string.Empty;
    public int Count { get; set; }
    public int MinScore { get; set; }
    public int MaxScore { get; set; }
}

public class RiskSummary
{
    public int TotalAssessments { get; set; }
    public int AllowedCount { get; set; }
    public int StepUpCount { get; set; }
    public int BlockedCount { get; set; }
    public double AvgRiskScore { get; set; }
    public int HighRiskCount { get; set; }
    public int TrustedDeviceCount { get; set; }
}
