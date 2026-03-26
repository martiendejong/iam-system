using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for bulk user import/export operations and data portability (GDPR Article 20).
/// Supports CSV/JSON formats with validation, dry-run mode, and progress tracking.
/// </summary>
public interface IBulkOperationService
{
    /// <summary>
    /// Import users from an uploaded file (CSV or JSON).
    /// Validates all rows, creates users, and tracks progress.
    /// </summary>
    Task<BulkOperation> ImportUsersAsync(
        Guid tenantId,
        Guid createdByUserId,
        Stream fileStream,
        string fileName,
        BulkOperationFormat format,
        bool dryRun = false,
        CancellationToken ct = default);

    /// <summary>
    /// Export all tenant user data (GDPR Article 20 - Right to data portability).
    /// Generates a downloadable file in the specified format.
    /// </summary>
    Task<BulkOperation> ExportUsersAsync(
        Guid tenantId,
        Guid createdByUserId,
        BulkOperationFormat format,
        CancellationToken ct = default);

    /// <summary>
    /// Get a paginated list of bulk operations for a tenant.
    /// </summary>
    Task<List<BulkOperation>> GetOperationsAsync(
        Guid tenantId,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default);

    /// <summary>
    /// Get a single bulk operation by ID (with progress info).
    /// </summary>
    Task<BulkOperation?> GetOperationAsync(
        Guid operationId,
        CancellationToken ct = default);

    /// <summary>
    /// Get the result file bytes for a completed export operation.
    /// </summary>
    Task<(byte[] Data, string ContentType, string FileName)?> GetOperationResultAsync(
        Guid operationId,
        CancellationToken ct = default);
}
