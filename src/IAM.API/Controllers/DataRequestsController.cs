using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/data-requests")]
[Authorize]
public class DataRequestsController : ControllerBase
{
    private readonly IDataRequestService _dataRequestService;
    private readonly ILogger<DataRequestsController> _logger;

    public DataRequestsController(IDataRequestService dataRequestService, ILogger<DataRequestsController> logger)
    {
        _dataRequestService = dataRequestService;
        _logger = logger;
    }

    /// <summary>
    /// Create a data export request for the current user (GDPR Article 15).
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> RequestExport(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var request = await _dataRequestService.CreateExportRequestAsync(userId.Value, ct);

        return Ok(new
        {
            id = request.Id,
            type = request.Type.ToString(),
            status = request.Status.ToString(),
            requestedAt = request.RequestedAt
        });
    }

    /// <summary>
    /// Create a data deletion request for the current user (GDPR Article 17).
    /// </summary>
    [HttpPost("deletion")]
    public async Task<IActionResult> RequestDeletion([FromBody] DeletionRequest? body, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var request = await _dataRequestService.CreateDeletionRequestAsync(userId.Value, body?.Reason, ct);

        return Ok(new
        {
            id = request.Id,
            type = request.Type.ToString(),
            status = request.Status.ToString(),
            requestedAt = request.RequestedAt
        });
    }

    /// <summary>
    /// Get the current user's data requests.
    /// </summary>
    [HttpGet("my-requests")]
    public async Task<IActionResult> GetMyRequests(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var requests = await _dataRequestService.GetRequestsAsync(userId.Value, ct);

        var response = requests.Select(r => new
        {
            id = r.Id,
            type = r.Type.ToString(),
            status = r.Status.ToString(),
            requestedAt = r.RequestedAt,
            completedAt = r.CompletedAt,
            notes = r.Notes,
            hasDownload = r.Type == DataRequestType.Export && r.DataUrl != null && r.ExpiresAt > DateTime.UtcNow
        });

        return Ok(response);
    }

    /// <summary>
    /// Get all pending data requests (admin only).
    /// </summary>
    [HttpGet("pending")]
    [Authorize(Roles = "SuperAdmin,SystemAdmin")]
    public async Task<IActionResult> GetPendingRequests(CancellationToken ct)
    {
        var requests = await _dataRequestService.GetPendingRequestsAsync(ct);

        var response = requests.Select(r => new
        {
            id = r.Id,
            userId = r.UserId,
            type = r.Type.ToString(),
            status = r.Status.ToString(),
            requestedAt = r.RequestedAt,
            notes = r.Notes
        });

        return Ok(response);
    }

    /// <summary>
    /// Process a pending data request (admin only).
    /// </summary>
    [HttpPost("{id:guid}/process")]
    [Authorize(Roles = "SuperAdmin,SystemAdmin")]
    public async Task<IActionResult> ProcessRequest(Guid id, CancellationToken ct)
    {
        var adminUserId = GetUserId();
        if (adminUserId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        try
        {
            // Determine the request type to call the right method
            var requests = await _dataRequestService.GetRequestsAsync(null, ct);
            var request = requests.FirstOrDefault(r => r.Id == id);

            if (request == null)
                return NotFound(new { error = "Data request not found" });

            DataRequest result;
            if (request.Type == DataRequestType.Export)
            {
                result = await _dataRequestService.ProcessExportAsync(id, ct);
            }
            else if (request.Type == DataRequestType.Deletion)
            {
                result = await _dataRequestService.ProcessDeletionAsync(id, adminUserId.Value, ct);
            }
            else
            {
                return BadRequest(new { error = $"Processing for request type '{request.Type}' is not yet implemented" });
            }

            return Ok(new
            {
                id = result.Id,
                type = result.Type.ToString(),
                status = result.Status.ToString(),
                requestedAt = result.RequestedAt,
                completedAt = result.CompletedAt,
                notes = result.Notes
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Download the exported data file (only if owned by current user and not expired).
    /// </summary>
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> DownloadExport(Guid id, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var requests = await _dataRequestService.GetRequestsAsync(userId.Value, ct);
        var request = requests.FirstOrDefault(r => r.Id == id);

        if (request == null)
            return NotFound(new { error = "Data request not found" });

        if (request.UserId != userId.Value)
            return Forbid();

        if (request.Type != DataRequestType.Export)
            return BadRequest(new { error = "This request is not an export request" });

        if (request.DataUrl == null)
            return BadRequest(new { error = "Export data is not yet available" });

        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value < DateTime.UtcNow)
            return BadRequest(new { error = "Export download link has expired" });

        // Decode the base64 data URL
        if (request.DataUrl.StartsWith("data:application/json;base64,"))
        {
            var base64 = request.DataUrl["data:application/json;base64,".Length..];
            var bytes = Convert.FromBase64String(base64);
            return File(bytes, "application/json", $"data-export-{userId.Value:N}.json");
        }

        // If stored as a regular URL (future blob storage), redirect
        return Redirect(request.DataUrl);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(claim, out var userId))
            return userId;

        return null;
    }
}

public record DeletionRequest(string? Reason);
