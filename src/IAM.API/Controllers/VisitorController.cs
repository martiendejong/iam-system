using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/visitors")]
[Authorize]
public class VisitorController : ControllerBase
{
    private readonly IVisitorService _visitorService;
    private readonly ILogger<VisitorController> _logger;

    public VisitorController(IVisitorService visitorService, ILogger<VisitorController> logger)
    {
        _visitorService = visitorService;
        _logger = logger;
    }

    /// <summary>
    /// Pre-register a new visitor.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> PreRegisterVisitor(
        [FromBody] PreRegisterVisitorRequest request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Visitor name is required" });

        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { error = "Visitor email is required" });

        if (request.TenantId == Guid.Empty)
            return BadRequest(new { error = "Tenant ID is required" });

        try
        {
            var visitor = await _visitorService.PreRegisterVisitorAsync(
                request.TenantId,
                request.HostUserId ?? userId.Value,
                request.Name,
                request.Email,
                request.Company,
                request.VisitDate,
                request.Purpose,
                request.AccessGrants,
                ct);

            return Ok(MapVisitorResponse(visitor));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pre-register visitor for tenant {TenantId}", request.TenantId);
            return StatusCode(500, new { error = "Failed to pre-register visitor" });
        }
    }

    /// <summary>
    /// Get a list of visitors for a tenant.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetVisitors(
        [FromQuery] Guid tenantId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        VisitorStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<VisitorStatus>(status, true, out var parsed))
        {
            statusFilter = parsed;
        }

        try
        {
            var visitors = await _visitorService.GetVisitorsAsync(tenantId, skip, take, statusFilter, ct);
            return Ok(visitors.Select(MapVisitorResponse));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visitors for tenant {TenantId}", tenantId);
            return StatusCode(500, new { error = "Failed to retrieve visitors" });
        }
    }

    /// <summary>
    /// Get a single visitor by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetVisitor(Guid id, CancellationToken ct)
    {
        var visitor = await _visitorService.GetVisitorAsync(id, ct);
        if (visitor == null)
            return NotFound(new { error = "Visitor not found" });

        return Ok(MapVisitorResponse(visitor));
    }

    /// <summary>
    /// Check in a visitor.
    /// </summary>
    [HttpPost("{id:guid}/checkin")]
    public async Task<IActionResult> CheckIn(Guid id, CancellationToken ct)
    {
        try
        {
            var visitor = await _visitorService.CheckInAsync(id, ct);
            if (visitor == null)
                return NotFound(new { error = "Visitor not found" });

            return Ok(MapVisitorResponse(visitor));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check in visitor {VisitorId}", id);
            return StatusCode(500, new { error = "Failed to check in visitor" });
        }
    }

    /// <summary>
    /// Check out a visitor.
    /// </summary>
    [HttpPost("{id:guid}/checkout")]
    public async Task<IActionResult> CheckOut(Guid id, CancellationToken ct)
    {
        try
        {
            var visitor = await _visitorService.CheckOutAsync(id, ct);
            if (visitor == null)
                return NotFound(new { error = "Visitor not found" });

            return Ok(MapVisitorResponse(visitor));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check out visitor {VisitorId}", id);
            return StatusCode(500, new { error = "Failed to check out visitor" });
        }
    }

    /// <summary>
    /// Get the QR code token for a visitor.
    /// </summary>
    [HttpGet("{id:guid}/qr")]
    public async Task<IActionResult> GetQrCode(Guid id, CancellationToken ct)
    {
        var qrToken = await _visitorService.GetQrTokenAsync(id, ct);
        if (qrToken == null)
            return NotFound(new { error = "Visitor not found" });

        return Ok(new { visitorId = id, qrToken });
    }

    // ---- Helpers ----

    private static object MapVisitorResponse(Visitor v) => new
    {
        id = v.Id,
        name = v.Name,
        email = v.Email,
        company = v.Company,
        hostUserId = v.HostUserId,
        hostUserName = v.HostUser != null ? $"{v.HostUser.FirstName} {v.HostUser.LastName}" : null,
        tenantId = v.TenantId,
        visitDate = v.VisitDate,
        checkInAt = v.CheckInAt,
        checkOutAt = v.CheckOutAt,
        status = v.Status.ToString(),
        qrToken = v.QrToken,
        purpose = v.Purpose,
        createdAt = v.CreatedAt,
        updatedAt = v.UpdatedAt,
        accessGrants = v.AccessGrants?.Select(g => new
        {
            id = g.Id,
            resources = g.Resources,
            validFrom = g.ValidFrom,
            validUntil = g.ValidUntil,
            isCurrentlyValid = g.IsCurrentlyValid()
        })
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

public record PreRegisterVisitorRequest(
    Guid TenantId,
    string Name,
    string Email,
    string? Company,
    DateTime VisitDate,
    string? Purpose,
    Guid? HostUserId,
    List<VisitorAccessGrantRequest>? AccessGrants);
