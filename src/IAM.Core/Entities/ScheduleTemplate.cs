using System.Text.Json;

namespace IAM.Core.Entities;

/// <summary>
/// Reusable schedule template for policies (business hours, shift patterns, etc.)
/// </summary>
public class ScheduleTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable name for this schedule
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of when this schedule applies
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Tenant this schedule belongs to (null = global/system schedule)
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Schedule type: BusinessHours, Shift, OnCall, Custom
    /// </summary>
    public string ScheduleType { get; set; } = "Custom";

    /// <summary>
    /// Timezone for this schedule (IANA format, e.g., "America/New_York")
    /// </summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>
    /// Recurring pattern in JSON format
    /// Example: { "type": "weekly", "daysOfWeek": [1,2,3,4,5], "startTime": "09:00", "endTime": "17:00" }
    /// </summary>
    public string RecurrencePattern { get; set; } = string.Empty;

    /// <summary>
    /// Exception dates (holidays, special closures) in JSON array
    /// Example: ["2026-12-25", "2026-01-01"]
    /// </summary>
    public string? ExceptionDates { get; set; }

    /// <summary>
    /// Additional inclusion dates (special opening days) in JSON array
    /// </summary>
    public string? InclusionDates { get; set; }

    /// <summary>
    /// Effective date range for this schedule
    /// </summary>
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    // Navigation properties
    public ICollection<Policy> Policies { get; set; } = new List<Policy>();

    /// <summary>
    /// Parse recurrence pattern from JSON
    /// </summary>
    public RecurrencePatternModel? GetRecurrencePattern()
    {
        if (string.IsNullOrWhiteSpace(RecurrencePattern))
            return null;

        return JsonSerializer.Deserialize<RecurrencePatternModel>(RecurrencePattern);
    }

    /// <summary>
    /// Check if a given datetime falls within this schedule
    /// </summary>
    public bool IsActiveAt(DateTime dateTime)
    {
        // Convert to schedule timezone
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(Timezone);
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(dateTime.ToUniversalTime(), timeZoneInfo);

        // Check effective date range
        if (EffectiveFrom.HasValue && localTime < EffectiveFrom.Value)
            return false;

        if (EffectiveTo.HasValue && localTime > EffectiveTo.Value)
            return false;

        // Check exception dates
        var exceptions = GetExceptionDates();
        if (exceptions.Contains(DateOnly.FromDateTime(localTime)))
            return false;

        // Check if date is in inclusion list (overrides recurrence pattern)
        var inclusions = GetInclusionDates();
        if (inclusions.Contains(DateOnly.FromDateTime(localTime)))
            return true;

        // Check recurrence pattern
        var pattern = GetRecurrencePattern();
        if (pattern == null)
            return true; // No pattern = always active

        return pattern.IsActiveAt(localTime);
    }

    private HashSet<DateOnly> GetExceptionDates()
    {
        if (string.IsNullOrWhiteSpace(ExceptionDates))
            return new HashSet<DateOnly>();

        var dates = JsonSerializer.Deserialize<string[]>(ExceptionDates) ?? Array.Empty<string>();
        return dates.Select(d => DateOnly.Parse(d)).ToHashSet();
    }

    private HashSet<DateOnly> GetInclusionDates()
    {
        if (string.IsNullOrWhiteSpace(InclusionDates))
            return new HashSet<DateOnly>();

        var dates = JsonSerializer.Deserialize<string[]>(InclusionDates) ?? Array.Empty<string>();
        return dates.Select(d => DateOnly.Parse(d)).ToHashSet();
    }
}

/// <summary>
/// Recurrence pattern model for schedule templates
/// </summary>
public class RecurrencePatternModel
{
    /// <summary>
    /// Pattern type: daily, weekly, monthly, yearly, custom, cron
    /// </summary>
    public string Type { get; set; } = "weekly";

    /// <summary>
    /// For weekly pattern: days of week (1=Monday, 7=Sunday)
    /// </summary>
    public List<int> DaysOfWeek { get; set; } = new();

    /// <summary>
    /// For monthly pattern: days of month (1-31)
    /// </summary>
    public List<int> DaysOfMonth { get; set; } = new();

    /// <summary>
    /// Start time in HH:mm format (24-hour)
    /// </summary>
    public string? StartTime { get; set; }

    /// <summary>
    /// End time in HH:mm format (24-hour)
    /// </summary>
    public string? EndTime { get; set; }

    /// <summary>
    /// For custom patterns: cron expression
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>
    /// Interval for daily pattern (e.g., every 2 days)
    /// </summary>
    public int Interval { get; set; } = 1;

    /// <summary>
    /// Check if a given datetime matches this recurrence pattern
    /// </summary>
    public bool IsActiveAt(DateTime dateTime)
    {
        // Check day of week for weekly pattern
        if (Type == "weekly" && DaysOfWeek.Count > 0)
        {
            var dayOfWeek = (int)dateTime.DayOfWeek;
            if (dayOfWeek == 0) dayOfWeek = 7; // Sunday = 7
            if (!DaysOfWeek.Contains(dayOfWeek))
                return false;
        }

        // Check day of month for monthly pattern
        if (Type == "monthly" && DaysOfMonth.Count > 0)
        {
            if (!DaysOfMonth.Contains(dateTime.Day))
                return false;
        }

        // Check time range
        if (!string.IsNullOrWhiteSpace(StartTime) && !string.IsNullOrWhiteSpace(EndTime))
        {
            var timeOfDay = dateTime.TimeOfDay;
            var startTime = TimeSpan.Parse(StartTime);
            var endTime = TimeSpan.Parse(EndTime);

            if (endTime < startTime)
            {
                // Crosses midnight (e.g., 22:00 to 02:00)
                return timeOfDay >= startTime || timeOfDay <= endTime;
            }
            else
            {
                return timeOfDay >= startTime && timeOfDay <= endTime;
            }
        }

        return true;
    }
}
