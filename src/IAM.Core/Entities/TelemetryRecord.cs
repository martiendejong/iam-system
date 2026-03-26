using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

public class TelemetryRecord
{
    public Guid Id { get; set; }

    [Required, MaxLength(255)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string MetricName { get; set; } = string.Empty;

    public double? NumericValue { get; set; }

    [MaxLength(1000)]
    public string? StringValue { get; set; }

    public string? JsonValue { get; set; } // JSONB for complex data

    [MaxLength(50)]
    public string? Unit { get; set; }

    public Guid? TenantId { get; set; }

    [MaxLength(100)]
    public string? DeviceType { get; set; }

    public string? Tags { get; set; } // JSONB

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
