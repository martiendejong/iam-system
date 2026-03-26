namespace IAM.Core.Entities;

/// <summary>
/// GDPR data subject request (export, deletion, rectification).
/// Supports GDPR Articles 15-17 (Right of access, Right to erasure).
/// </summary>
public class DataRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The user who made the request
    /// </summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Type of data request
    /// </summary>
    public DataRequestType Type { get; set; }

    /// <summary>
    /// Current processing status
    /// </summary>
    public DataRequestStatus Status { get; set; } = DataRequestStatus.Pending;

    /// <summary>
    /// When the request was submitted
    /// </summary>
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the request was fully processed
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Admin user who processed the request
    /// </summary>
    public Guid? ProcessedBy { get; set; }

    /// <summary>
    /// Processing notes or reason for rejection
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// URL for downloading the exported data (export requests only)
    /// </summary>
    public string? DataUrl { get; set; }

    /// <summary>
    /// When the export download link expires
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}

public enum DataRequestType
{
    Export,
    Deletion,
    Rectification
}

public enum DataRequestStatus
{
    Pending,
    Processing,
    Completed,
    Rejected
}
