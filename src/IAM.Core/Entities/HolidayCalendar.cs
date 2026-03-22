using System.Text.Json;

namespace IAM.Core.Entities;

/// <summary>
/// Holiday calendar for policy scheduling
/// Defines holidays, closures, and special dates that affect access policies
/// </summary>
public class HolidayCalendar
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Calendar name (e.g., "US Federal Holidays", "Company Holidays")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Calendar description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Tenant this calendar belongs to (null = global/system calendar)
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Country/region code (ISO 3166-1 alpha-2, e.g., "US", "NL", "GB")
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>
    /// Timezone for this calendar (IANA format)
    /// </summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>
    /// Holiday definitions in JSON array format
    /// Example: [{ "date": "2026-12-25", "name": "Christmas", "type": "public", "recurring": true }]
    /// </summary>
    public string Holidays { get; set; } = "[]";

    /// <summary>
    /// Whether this calendar is automatically updated from external source
    /// </summary>
    public bool IsAutoUpdated { get; set; } = false;

    /// <summary>
    /// External source URL for automatic updates (if applicable)
    /// </summary>
    public string? ExternalSourceUrl { get; set; }

    /// <summary>
    /// Last time holidays were updated from external source
    /// </summary>
    public DateTime? LastSyncedAt { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    // Navigation properties
    public ICollection<ScheduleTemplate> ScheduleTemplates { get; set; } = new List<ScheduleTemplate>();

    /// <summary>
    /// Parse holidays from JSON
    /// </summary>
    public List<HolidayDefinition> GetHolidays()
    {
        if (string.IsNullOrWhiteSpace(Holidays))
            return new List<HolidayDefinition>();

        return JsonSerializer.Deserialize<List<HolidayDefinition>>(Holidays) ?? new List<HolidayDefinition>();
    }

    /// <summary>
    /// Check if a given date is a holiday
    /// </summary>
    public bool IsHoliday(DateTime date)
    {
        var holidays = GetHolidays();
        var dateOnly = DateOnly.FromDateTime(date);

        foreach (var holiday in holidays)
        {
            if (holiday.Recurring)
            {
                // Check month and day only for recurring holidays
                var holidayDate = DateOnly.Parse(holiday.Date);
                if (holidayDate.Month == dateOnly.Month && holidayDate.Day == dateOnly.Day)
                    return true;
            }
            else
            {
                // Exact date match for non-recurring holidays
                if (DateOnly.Parse(holiday.Date) == dateOnly)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get holidays in a date range
    /// </summary>
    public List<HolidayDefinition> GetHolidaysInRange(DateTime startDate, DateTime endDate)
    {
        var holidays = GetHolidays();
        var result = new List<HolidayDefinition>();

        foreach (var holiday in holidays)
        {
            var holidayDate = DateOnly.Parse(holiday.Date);

            if (holiday.Recurring)
            {
                // For recurring holidays, check each year in the range
                for (int year = startDate.Year; year <= endDate.Year; year++)
                {
                    var dateInYear = new DateTime(year, holidayDate.Month, holidayDate.Day);
                    if (dateInYear >= startDate && dateInYear <= endDate)
                    {
                        result.Add(holiday);
                        break;
                    }
                }
            }
            else
            {
                // For non-recurring, check if within range
                var dateTime = holidayDate.ToDateTime(TimeOnly.MinValue);
                if (dateTime >= startDate && dateTime <= endDate)
                {
                    result.Add(holiday);
                }
            }
        }

        return result;
    }
}

/// <summary>
/// Individual holiday definition
/// </summary>
public class HolidayDefinition
{
    /// <summary>
    /// Holiday date in YYYY-MM-DD format
    /// </summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>
    /// Holiday name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Holiday type: public, religious, corporate, optional
    /// </summary>
    public string Type { get; set; } = "public";

    /// <summary>
    /// Whether this holiday recurs annually
    /// </summary>
    public bool Recurring { get; set; } = true;

    /// <summary>
    /// Optional description or notes
    /// </summary>
    public string? Description { get; set; }
}
