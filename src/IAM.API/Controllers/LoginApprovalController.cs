using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// Sign in on a new browser by approving on a device that is already signed in.
/// The new browser starts a request and shows a two-digit number (and a QR code); an already signed-in device
/// (the installed app, or the popup in any signed-in tab) shows the request and the user picks the matching number.
/// Then the new browser finishes the sign-in with the secret only it holds. See LoginApprovalService for the rules.
/// </summary>
[ApiController]
[Route("api/auth/approval")]
public class LoginApprovalController : ControllerBase
{
    private readonly ILoginApprovalService _approvals;
    private readonly IAuthService _authService;
    private readonly ILogger<LoginApprovalController> _logger;

    public LoginApprovalController(ILoginApprovalService approvals, IAuthService authService, ILogger<LoginApprovalController> logger)
    {
        _approvals = approvals;
        _authService = authService;
        _logger = logger;
    }

    // ----- the new browser (anonymous) -----

    /// <summary>
    /// Start a request. <c>email</c> is optional: with it the user's devices get a push notification and popup;
    /// without it only a device that scans the QR code can approve. The answer looks the same for unknown addresses.
    /// </summary>
    [HttpPost("request")]
    [AllowAnonymous]
    public async Task<IActionResult> Start([FromBody] StartApprovalRequest request, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();

        var started = await _approvals.StartAsync(request.Email, request.RememberMe, ip, userAgent, ct);
        if (started == null)
        {
            Response.Headers["Retry-After"] = "600";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many sign-in requests. Try again in a few minutes." });
        }

        return Ok(new
        {
            id = started.Id,
            pollSecret = started.PollSecret,
            matchNumber = started.MatchNumber,
            expiresAt = started.ExpiresAt,
            approvePath = $"/auth/approve/{started.Id}"
        });
    }

    /// <summary>Status for the browser that started the request: pending, approved, denied, expired or consumed.</summary>
    [HttpPost("{id:guid}/status")]
    [AllowAnonymous]
    public async Task<IActionResult> Status(Guid id, [FromBody] ApprovalSecretRequest request, CancellationToken ct)
    {
        var status = await _approvals.GetStatusAsync(id, request.Secret, ct);
        if (status == null)
        {
            return NotFound(new { error = "Unknown sign-in request" });
        }

        return Ok(new { status = status.Value.ToString().ToLowerInvariant() });
    }

    /// <summary>Finish the sign-in once the request is approved: sets the same cookies as a password sign-in.</summary>
    [HttpPost("{id:guid}/complete")]
    [AllowAnonymous]
    public async Task<IActionResult> Complete(Guid id, [FromBody] ApprovalSecretRequest request, CancellationToken ct)
    {
        var consumed = await _approvals.ConsumeAsync(id, request.Secret, ct);
        if (!consumed.Success || consumed.User == null)
        {
            return consumed.Status == LoginApprovalStatus.Pending
                ? Accepted(new { status = "pending" })
                : BadRequest(new { error = consumed.Error ?? "Sign-in request is no longer valid" });
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();

        // The approving device already passed this account's own sign-in (password / 2FA / passkey), so no second
        // factor is asked here; the network gate and the active check still apply inside LoginBypassPasswordAsync.
        var login = await _authService.LoginBypassPasswordAsync(consumed.User, ip, userAgent, consumed.RememberMe);
        if (!login.Success)
        {
            if (login.NetworkBlocked)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = login.Error });
            }

            return BadRequest(new { error = login.Error });
        }

        return await LoginCompletion.CompleteAsync(HttpContext, login, consumed.RememberMe);
    }

    // ----- the approving device (signed in) -----

    /// <summary>Requests that name the signed-in user and still wait (drives the popup).</summary>
    [HttpGet("pending")]
    [Authorize]
    public async Task<IActionResult> Pending(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        return Ok(await _approvals.ListPendingAsync(userId.Value, ct));
    }

    /// <summary>One request to approve: where it comes from and three numbers to choose from.</summary>
    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var info = await _approvals.GetForApproverAsync(id, userId.Value, ct);
        return info == null ? NotFound(new { error = "This sign-in request is no longer available" }) : Ok(info);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApproveRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var outcome = await _approvals.ApproveAsync(id, userId.Value, CurrentSession.RefreshTokenId(User), request.Number, ct);
        return outcome switch
        {
            ApproveOutcome.Approved => Ok(new { status = "approved" }),
            ApproveOutcome.WrongNumber => BadRequest(new { error = "That is not the number on the new device. Look at its screen and try again.", code = "wrong_number" }),
            ApproveOutcome.TooManyWrong => BadRequest(new { error = "Too many wrong numbers: the request was denied.", code = "denied" }),
            ApproveOutcome.NotFound => NotFound(new { error = "Unknown sign-in request" }),
            ApproveOutcome.WrongUser => NotFound(new { error = "Unknown sign-in request" }),
            _ => Conflict(new { error = "This sign-in request is no longer available", code = "not_pending" })
        };
    }

    [HttpPost("{id:guid}/deny")]
    [Authorize]
    public async Task<IActionResult> Deny(Guid id, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        return await _approvals.DenyAsync(id, userId.Value, ct)
            ? Ok(new { status = "denied" })
            : NotFound(new { error = "This sign-in request is no longer available" });
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}

public record StartApprovalRequest(string? Email, bool RememberMe = true);
public record ApprovalSecretRequest(string Secret);
public record ApproveRequest(int Number);
