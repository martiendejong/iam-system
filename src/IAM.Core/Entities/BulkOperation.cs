namespace IAM.Core.Entities;

/// <summary>
/// Tracks bulk import/export operations for user data portability.
/// Supports CSV/JSON formats, dry-run mode, and progress tracking.
/// GDPR Article 20 - Right to data portability.
/// </summary>
public class BulkOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant scope for this bulk operation
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Whether this is an import or export operation
    /// </summary>
    public BulkOperationType Type { get; set; }

    /// <summary>
    /// Current processing status
    /// </summary>
    public BulkOperationStatus Status { get; set; } = BulkOperationStatus.Pending;

    /// <summary>
    /// File format (CSV or JSON)
    /// </summary>
    public BulkOperationFormat Format { get; set; }

    /// <summary>
    /// Total number of rows/records to process
    /// </summary>
    public int TotalRows { get; set; }

    /// <summary>
    /// Number of rows processed so far
    /// </summary>
    public int ProcessedRows { get; set; }

    /// <summary>
    /// Number of rows successfully processed
    /// </summary>
    public int SuccessRows { get; set; }

    /// <summary>
    /// Number of rows that failed processing
    /// </summary>
    public int ErrorRows { get; set; }

    /// <summary>
    /// JSON array of error details per row
    /// </summary>
    public string? ErrorDetails { get; set; }

    /// <summary>
    /// Original uploaded file name (imports) or generated file name (exports)
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Base64 data URL or external URL of the result file (exports/error reports)
    /// </summary>
    public string? ResultUrl { get; set; }

    /// <summary>
    /// If true, import validates only without persisting changes
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// User who initiated this operation
    /// </summary>
    public Guid CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>
    /// When processing started
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When processing completed (success or failure)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum BulkOperationType
{
    Import,
    Export
}

public enum BulkOperationStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

public enum BulkOperationFormat
{
    CSV,
    JSON
}
