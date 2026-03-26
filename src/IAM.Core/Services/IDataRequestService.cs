using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing GDPR data subject requests (export, deletion, rectification).
/// </summary>
public interface IDataRequestService
{
    /// <summary>
    /// Create a data export request for a user (GDPR Article 15 - Right of access).
    /// </summary>
    Task<DataRequest> CreateExportRequestAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Create a data deletion request for a user (GDPR Article 17 - Right to erasure).
    /// </summary>
    Task<DataRequest> CreateDeletionRequestAsync(Guid userId, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Process a data export request: generate JSON export of all user data.
    /// </summary>
    Task<DataRequest> ProcessExportAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Process a data deletion request: anonymize user data while preserving audit trail integrity.
    /// </summary>
    Task<DataRequest> ProcessDeletionAsync(Guid requestId, Guid processedBy, CancellationToken ct = default);

    /// <summary>
    /// Get data requests, optionally filtered by user.
    /// </summary>
    Task<List<DataRequest>> GetRequestsAsync(Guid? userId, CancellationToken ct = default);

    /// <summary>
    /// Get all pending data requests awaiting admin processing.
    /// </summary>
    Task<List<DataRequest>> GetPendingRequestsAsync(CancellationToken ct = default);
}
