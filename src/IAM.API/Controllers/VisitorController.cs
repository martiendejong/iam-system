using System.Security.Claims;
using Hazina.Security.ApiKeys;
using IAM.API.Authorization;
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
    /// <summary>Largest page a list request may ask for.</summary>
    internal const int MaxPageSize = 100;

    private const int DefaultPageSize = 20;

    private readonly IVisitorService _visitorService;
    private readonly ITenantAccessResolver _access;
    private readonly ILogger<VisitorController> _logger;

    public VisitorController(
        IVisitorService visitorService,
        ITenantAccessResolver access,
        ILogger<VisitorController> logger)
    {
        _visitorService = visitorService;
        _access = access;
        _logger = logger;
    }

    // Task 5151. A visitor holds names, e-mails, companies and the QR token (the door-access credential), so every
    // action is checked against the tenant of the visitor. Tenant managers (TenantAdmin/BuildingOwner/BuildingManager
    // UserRoles row, SuperAdmin, write/admin API keys) do everything for their tenant's visitors; a plain tenant member
    // registers visitors hosted by themselves and sees, checks in, checks out and gets the QR of visitors they host.
    // The tenant of an existing visitor always comes from the stored row, never from the request, and a visitor the
    // caller may not act on is reported exactly like an unknown id (404). Device, service-account and API keys that
    // cannot change their tenant (read scope) are refused: a QR token is a credential, not data to read.

    private ObjectResult ForbiddenVisitors() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "You do not have access to the visitors of this tenant." });

    private ObjectResult ForbiddenRegistration() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "Only a manager of the tenant can register visitors, or a member registering visitors they host themselves." });

    /// <summary>
    /// The caller's tenants, or null when the caller may not use the visitor API at all (device and service-account
    /// tokens, tokens without a usable identity, API keys that cannot change their tenant).
    /// </summary>
    private async Task<TenantAccess?> ResolveAccessAsync(CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused)
            return null;

        if (User.IsApiKey() && !access.CanManageAny)
            return null;

        return access;
    }

    /// <summary>
    /// A manager of the visitor's tenant, or the member who hosts the visitor (never an API key: a key is not a host).
    /// </summary>
    private bool CanActOn(TenantAccess access, Visitor visitor)
    {
        if (access.CanManage(visitor.TenantId))
            return true;

        return !User.IsApiKey()
            && access.CanRead(visitor.TenantId)
            && GetUserId() is { } callerId
            && visitor.HostUserId == callerId;
    }

    /// <summary>
    /// Authorizes the caller, then loads the visitor with the tenant it is stored under. Exactly one of the two
    /// results is set: the visitor, or the 403/404 to return.
    /// </summary>
    private async Task<(Visitor? Visitor, IActionResult? Failure)> LoadAuthorizedVisitorAsync(Guid id, CancellationToken ct)
    {
        var access = await ResolveAccessAsync(ct);
        if (access == null)
            return (null, ForbiddenVisitors());

        var visitor = await _visitorService.GetVisitorAsync(id, ct);
        if (visitor == null || !CanActOn(access, visitor))
            return (null, NotFound(new { error = "Visitor not found" }));

        return (visitor, null);
    }

    /// <summary>
    /// Pre-register a new visitor.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> PreRegisterVisitor(
        [FromBody] PreRegisterVisitorRequest request,
        CancellationToken ct)
    {
        // Authorize before anything else: the tenant comes from the body, so it is the one thing to distrust.
        var access = await ResolveAccessAsync(ct);
        if (access == null)
            return ForbiddenVisitors();

        var userId = GetUserId();
        var isManager = access.CanManage(request.TenantId);
        var registersAsOwnHost = !User.IsApiKey()
            && userId != null
            && access.CanRead(request.TenantId)
            && (request.HostUserId == null || request.HostUserId == userId);
        if (!isManager && !registersAsOwnHost)
            return ForbiddenRegistration();

        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Visitor name is required" });

        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { error = "Visitor email is required" });

        if (request.TenantId == Guid.Empty)
            return BadRequest(new { error = "Tenant ID is required" });

        if (request.AccessGrants != null
            && request.AccessGrants.Any(g => g == null || g.Resources == null || g.ValidUntil <= g.ValidFrom))
        {
            return BadRequest(new { error = "Every access grant needs resources and a validity window that ends after it starts" });
        }

        // The host must belong to the tenant. A caller hosting themselves was already shown to be a member (or
        // SuperAdmin) by the access check, so only another user is looked up.
        var hostUserId = request.HostUserId ?? userId.Value;
        var callerHostsThemselves = hostUserId == userId && !User.IsApiKey() && access.CanRead(request.TenantId);
        if (!callerHostsThemselves && !await _visitorService.IsTenantMemberAsync(hostUserId, request.TenantId, ct))
            return BadRequest(new { error = "The host must be a member of the tenant" });

        try
        {
            var visitor = await _visitorService.PreRegisterVisitorAsync(
                request.TenantId,
                hostUserId,
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
    /// Get a list of visitors for a tenant. Managers see every visitor of the tenant, other members only the
    /// visitors they host.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetVisitors(
        [FromQuery] Guid tenantId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var access = await ResolveAccessAsync(ct);
        if (access == null || !access.CanRead(tenantId))
            return ForbiddenVisitors();

        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "Tenant ID is required" });

        // Not a manager of the tenant: pin the list to the visitors the caller hosts.
        Guid? hostFilter = null;
        if (!access.CanManage(tenantId))
        {
            if (User.IsApiKey() || GetUserId() is not { } callerId)
                return ForbiddenVisitors();

            hostFilter = callerId;
        }

        VisitorStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<VisitorStatus>(status, true, out var parsed))
        {
            statusFilter = parsed;
        }

        skip = Math.Max(skip, 0);
        take = take < 1 ? DefaultPageSize : Math.Min(take, MaxPageSize);

        try
        {
            var visitors = await _visitorService.GetVisitorsAsync(tenantId, skip, take, statusFilter, hostFilter, ct);
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
        var (visitor, failure) = await LoadAuthorizedVisitorAsync(id, ct);
        if (failure != null)
            return failure;

        return Ok(MapVisitorResponse(visitor!));
    }

    /// <summary>
    /// Check in a visitor.
    /// </summary>
    [HttpPost("{id:guid}/checkin")]
    public async Task<IActionResult> CheckIn(Guid id, CancellationToken ct)
    {
        var (_, failure) = await LoadAuthorizedVisitorAsync(id, ct);
        if (failure != null)
            return failure;

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
        var (_, failure) = await LoadAuthorizedVisitorAsync(id, ct);
        if (failure != null)
            return failure;

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
        var (visitor, failure) = await LoadAuthorizedVisitorAsync(id, ct);
        if (failure != null)
            return failure;

        return Ok(new { visitorId = id, qrToken = visitor!.QrToken });
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
