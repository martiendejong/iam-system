using Fido2NetLib;
using Fido2NetLib.Objects;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PasskeyController : ControllerBase
{
    private readonly IPasskeyService _passkeyService;
    private readonly IAuthService _authService;
    private readonly IAMDbContext _context;
    private readonly ILogger<PasskeyController> _logger;

    public PasskeyController(
        IPasskeyService passkeyService,
        IAuthService authService,
        IAMDbContext context,
        ILogger<PasskeyController> logger)
    {
        _passkeyService = passkeyService;
        _authService = authService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Begin passkey registration for a user
    /// </summary>
    [HttpPost("register/begin")]
    [Authorize]
    public async Task<ActionResult<CredentialCreateOptions>> BeginRegistration(
        [FromBody] BeginRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get user ID from claims
            var userIdClaim = User.FindFirst("sub") ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            var options = await _passkeyService.BeginRegistrationAsync(
                userId,
                request.Username,
                request.DisplayName,
                cancellationToken);

            return Ok(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error beginning passkey registration");
            return StatusCode(500, new { error = "Failed to begin registration" });
        }
    }

    /// <summary>
    /// Complete passkey registration
    /// </summary>
    [HttpPost("register/complete")]
    [Authorize]
    public async Task<ActionResult> CompleteRegistration(
        [FromBody] CompleteRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get user ID from claims
            var userIdClaim = User.FindFirst("sub") ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            var success = await _passkeyService.CompleteRegistrationAsync(
                userId,
                request.CredentialName,
                request.AttestationResponse,
                cancellationToken);

            if (!success)
            {
                return BadRequest(new { error = "Failed to verify passkey registration" });
            }

            return Ok(new { message = "Passkey registered successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey registration");
            return StatusCode(500, new { error = "Failed to complete registration" });
        }
    }

    /// <summary>
    /// Begin passkey authentication
    /// </summary>
    [HttpPost("authenticate/begin")]
    [AllowAnonymous]
    public async Task<ActionResult<AssertionOptions>> BeginAuthentication(
        [FromBody] BeginAuthenticationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = await _passkeyService.BeginAuthenticationAsync(
                request.Username,
                cancellationToken);

            return Ok(options);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Authentication begin failed for user {Username}", request.Username);
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error beginning passkey authentication");
            return StatusCode(500, new { error = "Failed to begin authentication" });
        }
    }

    /// <summary>
    /// Complete passkey authentication and get JWT token
    /// </summary>
    [HttpPost("authenticate/complete")]
    [AllowAnonymous]
    public async Task<ActionResult> CompleteAuthentication(
        [FromBody] AuthenticatorAssertionRawResponse assertionResponse,
        CancellationToken cancellationToken)
    {
        try
        {
            var userId = await _passkeyService.CompleteAuthenticationAsync(
                assertionResponse,
                cancellationToken);

            if (userId == null)
            {
                return Unauthorized(new { error = "Passkey authentication failed" });
            }

            var user = await _context.Users
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);

            if (user == null || !user.IsActive)
            {
                return Unauthorized(new { error = "Passkey authentication failed" });
            }

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].ToString();
            var loginResult = await _authService.LoginBypassPasswordAsync(user, ipAddress, userAgent);

            if (!loginResult.Success)
            {
                return Unauthorized(new { error = loginResult.Error });
            }

            Response.Cookies.Append("refreshToken", loginResult.RefreshToken!, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddDays(loginResult.RefreshTokenLifetimeDays)
            });

            return Ok(new
            {
                accessToken = loginResult.AccessToken,
                user = new
                {
                    id = loginResult.User!.Id,
                    email = loginResult.User.Email,
                    firstName = loginResult.User.FirstName,
                    lastName = loginResult.User.LastName
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey authentication");
            return StatusCode(500, new { error = "Failed to complete authentication" });
        }
    }

    /// <summary>
    /// Get all passkeys registered for the authenticated user
    /// </summary>
    [HttpGet("credentials")]
    [Authorize]
    public async Task<ActionResult> GetCredentials(CancellationToken cancellationToken)
    {
        try
        {
            var userIdClaim = User.FindFirst("sub") ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            var credentials = await _passkeyService.GetUserCredentialsAsync(userId, cancellationToken);
            return Ok(credentials);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user credentials");
            return StatusCode(500, new { error = "Failed to retrieve credentials" });
        }
    }

    /// <summary>
    /// Delete a passkey
    /// </summary>
    [HttpDelete("credentials/{credentialId}")]
    [Authorize]
    public async Task<ActionResult> DeleteCredential(
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        try
        {
            var userIdClaim = User.FindFirst("sub") ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            var success = await _passkeyService.DeleteCredentialAsync(userId, credentialId, cancellationToken);

            if (!success)
            {
                return NotFound(new { error = "Credential not found" });
            }

            return Ok(new { message = "Credential deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting credential");
            return StatusCode(500, new { error = "Failed to delete credential" });
        }
    }

    /// <summary>
    /// Rename a passkey
    /// </summary>
    [HttpPatch("credentials/{credentialId}")]
    [Authorize]
    public async Task<ActionResult> RenameCredential(
        Guid credentialId,
        [FromBody] RenameCredentialRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var userIdClaim = User.FindFirst("sub") ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            var success = await _passkeyService.RenameCredentialAsync(
                userId,
                credentialId,
                request.NewName,
                cancellationToken);

            if (!success)
            {
                return NotFound(new { error = "Credential not found" });
            }

            return Ok(new { message = "Credential renamed successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming credential");
            return StatusCode(500, new { error = "Failed to rename credential" });
        }
    }
}

// Request DTOs
public record BeginRegistrationRequest(string Username, string DisplayName);
public record CompleteRegistrationRequest(string CredentialName, AuthenticatorAttestationRawResponse AttestationResponse);
public record BeginAuthenticationRequest(string Username);
public record RenameCredentialRequest(string NewName);
