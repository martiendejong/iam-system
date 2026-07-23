using System.Security.Claims;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/consent")]
[Authorize]
public class ConsentController : ControllerBase
{
    private readonly IConsentService _consentService;
    private readonly ILogger<ConsentController> _logger;

    public ConsentController(IConsentService consentService, ILogger<ConsentController> logger)
    {
        _consentService = consentService;
        _logger = logger;
    }

    /// <summary>
    /// Get all active consent records for the current user.
    /// </summary>
    [HttpGet("my-consents")]
    public async Task<IActionResult> GetMyConsents(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var consents = await _consentService.GetConsentsAsync(userId.Value, ct);

        var response = consents.Select(c => new
        {
            id = c.Id,
            clientId = c.ClientId,
            scopes = c.Scopes,
            grantedAt = c.GrantedAt,
            ipAddress = c.IpAddress,
            userAgent = c.UserAgent
        });

        return Ok(response);
    }

    /// <summary>
    /// Grant consent for an OAuth2 client to access specific scopes.
    /// </summary>
    [HttpPost("grant")]
    public async Task<IActionResult> GrantConsent([FromBody] GrantConsentRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        if (string.IsNullOrWhiteSpace(request.ClientId))
            return BadRequest(new { error = "ClientId is required" });

        if (string.IsNullOrWhiteSpace(request.Scopes))
            return BadRequest(new { error = "Scopes is required" });

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.FirstOrDefault();

        var consent = await _consentService.GrantConsentAsync(userId.Value, request.ClientId, request.Scopes, ipAddress, userAgent, ct);

        return Ok(new
        {
            id = consent.Id,
            clientId = consent.ClientId,
            scopes = consent.Scopes,
            grantedAt = consent.GrantedAt
        });
    }

    /// <summary>
    /// Revoke consent for an OAuth2 client.
    /// </summary>
    [HttpPost("revoke")]
    public async Task<IActionResult> RevokeConsent([FromBody] RevokeConsentRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        if (string.IsNullOrWhiteSpace(request.ClientId))
            return BadRequest(new { error = "ClientId is required" });

        var success = await _consentService.RevokeConsentAsync(userId.Value, request.ClientId, ct);

        if (!success)
            return NotFound(new { error = "No active consent found for this client" });

        return Ok(new { message = "Consent revoked successfully" });
    }

    /// <summary>
    /// Check if the current user has consented to the requested scopes for a client.
    /// </summary>
    [HttpGet("check")]
    public async Task<IActionResult> CheckConsent([FromQuery] string clientId, [FromQuery] string scopes, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(scopes))
            return BadRequest(new { error = "clientId and scopes query parameters are required" });

        var hasConsent = await _consentService.HasConsentAsync(userId.Value, clientId, scopes, ct);

        return Ok(new { hasConsent });
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

public record GrantConsentRequest(string ClientId, string Scopes);
public record RevokeConsentRequest(string ClientId);
