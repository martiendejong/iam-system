using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/bulk")]
[Authorize(Roles = "SuperAdmin,SystemAdmin")]
public class BulkOperationsController : ControllerBase
{
    private readonly IBulkOperationService _bulkOperationService;
    private readonly ILogger<BulkOperationsController> _logger;

    public BulkOperationsController(IBulkOperationService bulkOperationService, ILogger<BulkOperationsController> logger)
    {
        _bulkOperationService = bulkOperationService;
        _logger = logger;
    }

    /// <summary>
    /// Import users from an uploaded file (CSV or JSON).
    /// </summary>
    [HttpPost("import")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB max
    public async Task<IActionResult> ImportUsers(
        [FromForm] IFormFile file,
        [FromForm] Guid tenantId,
        [FromForm] string? format,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded" });

        var bulkFormat = ResolveFormat(file.FileName, format);
        if (bulkFormat == null)
            return BadRequest(new { error = "Unsupported file format. Use .csv or .json" });

        try
        {
            using var stream = file.OpenReadStream();
            var operation = await _bulkOperationService.ImportUsersAsync(
                tenantId, userId.Value, stream, file.FileName, bulkFormat.Value, dryRun: false, ct);

            return Ok(MapOperationResponse(operation));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk import failed for tenant {TenantId}", tenantId);
            return StatusCode(500, new { error = "Import operation failed unexpectedly" });
        }
    }

    /// <summary>
    /// Dry-run import: validate file without creating users.
    /// </summary>
    [HttpPost("import/dry-run")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> ImportUsersDryRun(
        [FromForm] IFormFile file,
        [FromForm] Guid tenantId,
        [FromForm] string? format,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded" });

        var bulkFormat = ResolveFormat(file.FileName, format);
        if (bulkFormat == null)
            return BadRequest(new { error = "Unsupported file format. Use .csv or .json" });

        try
        {
            using var stream = file.OpenReadStream();
            var operation = await _bulkOperationService.ImportUsersAsync(
                tenantId, userId.Value, stream, file.FileName, bulkFormat.Value, dryRun: true, ct);

            return Ok(MapOperationResponse(operation));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk import dry-run failed for tenant {TenantId}", tenantId);
            return StatusCode(500, new { error = "Dry-run operation failed unexpectedly" });
        }
    }

    /// <summary>
    /// Export all tenant user data (GDPR Article 20).
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> ExportUsers(
        [FromBody] ExportRequest request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var format = request.Format?.ToLowerInvariant() switch
        {
            "json" => BulkOperationFormat.JSON,
            "csv" => BulkOperationFormat.CSV,
            _ => BulkOperationFormat.CSV
        };

        try
        {
            var operation = await _bulkOperationService.ExportUsersAsync(
                request.TenantId, userId.Value, format, ct);

            return Ok(MapOperationResponse(operation));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk export failed for tenant {TenantId}", request.TenantId);
            return StatusCode(500, new { error = "Export operation failed unexpectedly" });
        }
    }

    /// <summary>
    /// List bulk operations for a tenant.
    /// </summary>
    [HttpGet("operations")]
    public async Task<IActionResult> GetOperations(
        [FromQuery] Guid tenantId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        var operations = await _bulkOperationService.GetOperationsAsync(tenantId, skip, take, ct);

        var response = operations.Select(MapOperationResponse);
        return Ok(response);
    }

    /// <summary>
    /// Get a single operation by ID (status + progress).
    /// </summary>
    [HttpGet("operations/{id:guid}")]
    public async Task<IActionResult> GetOperation(Guid id, CancellationToken ct)
    {
        var operation = await _bulkOperationService.GetOperationAsync(id, ct);
        if (operation == null)
            return NotFound(new { error = "Bulk operation not found" });

        return Ok(MapOperationResponse(operation));
    }

    /// <summary>
    /// Download the result file for a completed export operation.
    /// </summary>
    [HttpGet("operations/{id:guid}/download")]
    public async Task<IActionResult> DownloadResult(Guid id, CancellationToken ct)
    {
        var result = await _bulkOperationService.GetOperationResultAsync(id, ct);
        if (result == null)
            return NotFound(new { error = "Result file not available" });

        return File(result.Value.Data, result.Value.ContentType, result.Value.FileName);
    }

    // ---- Helpers ----

    private static BulkOperationFormat? ResolveFormat(string fileName, string? formatHint)
    {
        if (!string.IsNullOrEmpty(formatHint))
        {
            return formatHint.ToLowerInvariant() switch
            {
                "csv" => BulkOperationFormat.CSV,
                "json" => BulkOperationFormat.JSON,
                _ => null
            };
        }

        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BulkOperationFormat.CSV;
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return BulkOperationFormat.JSON;

        return null;
    }

    private static object MapOperationResponse(BulkOperation op) => new
    {
        id = op.Id,
        tenantId = op.TenantId,
        type = op.Type.ToString(),
        status = op.Status.ToString(),
        format = op.Format.ToString(),
        totalRows = op.TotalRows,
        processedRows = op.ProcessedRows,
        successRows = op.SuccessRows,
        errorRows = op.ErrorRows,
        errorDetails = op.ErrorDetails,
        fileName = op.FileName,
        dryRun = op.DryRun,
        hasResult = op.ResultUrl != null,
        createdByUserId = op.CreatedByUserId,
        startedAt = op.StartedAt,
        completedAt = op.CompletedAt,
        createdAt = op.CreatedAt
    };

    private Guid? GetUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(claim, out var userId))
            return userId;

        return null;
    }
}

public record ExportRequest(Guid TenantId, string? Format);
