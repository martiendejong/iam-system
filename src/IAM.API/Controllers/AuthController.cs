using System.Security.Claims;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var result = await _authService.RegisterAsync(
            request.Email,
            request.Password,
            request.FirstName,
            request.LastName
        );

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(new
        {
            message = "Registration successful. Please check your email to verify your account.",
            userId = result.User!.Id
        });
    }

    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request)
    {
        var success = await _authService.VerifyEmailAsync(request.Token);

        if (!success)
        {
            return BadRequest(new { error = "Invalid or expired verification token" });
        }

        return Ok(new { message = "Email verified successfully. You can now log in." });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        // Extract device fingerprinting information
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();

        var result = await _authService.LoginAsync(request.Email, request.Password, ipAddress, userAgent);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        if (result.RequiresStepUp)
        {
            return Ok(new
            {
                requiresStepUp = true,
                userId = result.User!.Id,
                message = "Additional verification required. Check your email for a code."
            });
        }

        if (result.RequiresTwoFactor)
        {
            return Ok(new
            {
                requiresTwoFactor = true,
                userId = result.User!.Id,
                message = "A verification code has been sent to your email."
            });
        }

        return await CompleteLoginAsync(result);
    }

    [HttpPost("2fa/verify")]
    public async Task<IActionResult> VerifyTwoFactor([FromBody] TwoFactorVerifyRequest request)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();

        var result = await _authService.VerifyLoginTwoFactorAsync(request.UserId, request.Code, ipAddress, userAgent);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return await CompleteLoginAsync(result);
    }

    [HttpPost("2fa/resend")]
    public async Task<IActionResult> ResendTwoFactorCode([FromBody] ResendTwoFactorRequest request)
    {
        await _authService.ResendLoginTwoFactorCodeAsync(request.UserId);

        // Always return success (don't reveal account state to an unauthenticated caller)
        return Ok(new { message = "If two-factor authentication is enabled for this account, a new code has been sent." });
    }

    private async Task<IActionResult> CompleteLoginAsync(AuthResult result)
    {
        // Set refresh token in HttpOnly cookie
        Response.Cookies.Append("refreshToken", result.RefreshToken!, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Establish OIDC session cookie so the authorize endpoint can identify the user
        // without requiring a Bearer token in the browser request
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.User!.Id.ToString()),
            new(ClaimTypes.Email, result.User.Email),
            new(ClaimTypes.Name, $"{result.User.FirstName} {result.User.LastName}".Trim()),
        };
        var identity = new ClaimsIdentity(claims, "IAM.Session");
        await HttpContext.SignInAsync("IAM.Session", new ClaimsPrincipal(identity));

        return Ok(new
        {
            accessToken = result.AccessToken,
            user = new
            {
                id = result.User!.Id,
                email = result.User.Email,
                firstName = result.User.FirstName,
                lastName = result.User.LastName
            }
        });
    }

    /// <summary>
    /// Completes a login that was suspended for adaptive-MFA step-up verification
    /// (see /login's requiresStepUp response) by validating the emailed code.
    /// </summary>
    [HttpPost("step-up/verify")]
    public async Task<IActionResult> VerifyStepUp([FromBody] StepUpVerifyRequest request)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();

        var result = await _authService.VerifyStepUpAsync(request.Email, request.Code, ipAddress, userAgent);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return await CompleteLoginAsync(result);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (!Request.Cookies.TryGetValue("refreshToken", out var refreshToken))
        {
            return Unauthorized(new { error = "Refresh token not found" });
        }

        // Extract device fingerprinting information
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();

        var result = await _authService.RefreshTokenAsync(refreshToken, ipAddress, userAgent);

        if (!result.Success)
        {
            return Unauthorized(new { error = result.Error });
        }

        // SINGLE-USE TOKENS: Update cookie with NEW refresh token (token rotation)
        Response.Cookies.Append("refreshToken", result.RefreshToken!, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        });

        return Ok(new
        {
            accessToken = result.AccessToken
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (Request.Cookies.TryGetValue("refreshToken", out var refreshToken))
        {
            await _authService.RevokeTokenAsync(refreshToken);
        }

        Response.Cookies.Delete("refreshToken");
        await HttpContext.SignOutAsync("IAM.Session");

        return Ok(new { message = "Logged out successfully" });
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        await _authService.SendPasswordResetAsync(request.Email);

        // Always return success (don't reveal if email exists)
        return Ok(new { message = "If the email exists, a password reset link has been sent." });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var success = await _authService.ResetPasswordAsync(request.Token, request.NewPassword);

        if (!success)
        {
            return BadRequest(new { error = "Invalid or expired reset token" });
        }

        return Ok(new { message = "Password reset successfully. You can now log in with your new password." });
    }
}

public record RegisterRequest(string Email, string Password, string FirstName, string LastName);
public record LoginRequest(string Email, string Password);
public record VerifyEmailRequest(string Token);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);
public record StepUpVerifyRequest(string Email, string Code);
public record TwoFactorVerifyRequest(Guid UserId, string Code);
public record ResendTwoFactorRequest(Guid UserId);
