using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/auth/magic-link")]
public class MagicLinkController : ControllerBase
{
    private readonly IMagicLinkService _magicLinkService;
    private readonly IAuthService _authService;

    public MagicLinkController(IMagicLinkService magicLinkService, IAuthService authService)
    {
        _magicLinkService = magicLinkService;
        _authService = authService;
    }

    [HttpPost("request")]
    public async Task<IActionResult> RequestMagicLink([FromBody] MagicLinkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { error = "Email is required" });
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _magicLinkService.SendMagicLinkAsync(request.Email, MagicLinkPurpose.Login, ipAddress, request.ReturnUrl);

        if (!result)
        {
            return StatusCode(429, new { error = "Too many requests. Please try again later." });
        }

        // Always return success to not reveal if email exists
        return Ok(new { message = "If the email exists, a magic link has been sent." });
    }

    [HttpPost("verify")]
    public async Task<IActionResult> VerifyMagicLink([FromBody] MagicLinkVerifyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { error = "Token is required" });
        }

        var user = await _magicLinkService.ValidateMagicLinkAsync(request.Token);

        if (user == null)
        {
            return BadRequest(new { error = "Invalid or expired magic link" });
        }

        // Generate JWT tokens using the same flow as password login. If the account has
        // email 2FA enabled, this suspends the login and emails a second-factor code
        // instead of signing the user in immediately (see AuthResult.RequiresTwoFactor) —
        // a magic link only proves email possession, not the second factor.
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();
        var loginResult = await _authService.CompletePasswordlessLoginAsync(user, ipAddress, userAgent);

        if (!loginResult.Success)
        {
            return BadRequest(new { error = loginResult.Error });
        }

        if (loginResult.RequiresTwoFactor)
        {
            return Ok(new
            {
                requiresTwoFactor = true,
                userId = loginResult.User!.Id,
                message = "A verification code has been sent to your email."
            });
        }

        // Set refresh token in HttpOnly cookie (same as AuthController.Login)
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
}

public record MagicLinkRequest(string Email, string? ReturnUrl = null);
public record MagicLinkVerifyRequest(string Token);
