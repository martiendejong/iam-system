using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/auth/otp")]
public class OtpController : ControllerBase
{
    private readonly IOtpService _otpService;
    private readonly IAuthService _authService;
    private readonly IAMDbContext _context;

    public OtpController(IOtpService otpService, IAuthService authService, IAMDbContext context)
    {
        _otpService = otpService;
        _authService = authService;
        _context = context;
    }

    [HttpPost("email/request")]
    public async Task<IActionResult> RequestEmailOtp([FromBody] EmailOtpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { error = "Email is required" });
        }

        var result = await _otpService.SendEmailOtpAsync(request.Email, OtpPurpose.Login);

        if (!result)
        {
            return StatusCode(429, new { error = "Too many requests. Please try again later." });
        }

        return Ok(new { message = "If the email exists, a verification code has been sent." });
    }

    [HttpPost("email/verify")]
    public async Task<IActionResult> VerifyEmailOtp([FromBody] EmailOtpVerifyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { error = "Email and code are required" });
        }

        var valid = await _otpService.ValidateOtpAsync(request.Email, null, request.Code, OtpPurpose.Login);

        if (!valid)
        {
            return BadRequest(new { error = "Invalid or expired verification code" });
        }

        // Find user and generate JWT tokens
        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == request.Email);

        if (user == null || !user.IsActive)
        {
            return BadRequest(new { error = "Invalid or expired verification code" });
        }

        // Generate JWT tokens using the same flow as password login. If the account has
        // email 2FA enabled, this suspends the login and emails a second-factor code
        // instead of signing the user in immediately (see AuthResult.RequiresTwoFactor) —
        // an email OTP only proves email possession, not the second factor.
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

        Response.Cookies.Append("refreshToken", loginResult.RefreshToken!, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(7)
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

    [HttpPost("sms/request")]
    public async Task<IActionResult> RequestSmsOtp([FromBody] SmsOtpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            return BadRequest(new { error = "Phone number is required" });
        }

        var result = await _otpService.SendSmsOtpAsync(request.PhoneNumber, OtpPurpose.Login);

        if (!result)
        {
            return StatusCode(429, new { error = "Too many requests. Please try again later." });
        }

        return Ok(new { message = "If the phone number is registered, a verification code has been sent." });
    }

    [HttpPost("sms/verify")]
    public async Task<IActionResult> VerifySmsOtp([FromBody] SmsOtpVerifyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber) || string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { error = "Phone number and code are required" });
        }

        var valid = await _otpService.ValidateOtpAsync(null, request.PhoneNumber, request.Code, OtpPurpose.Login);

        if (!valid)
        {
            return BadRequest(new { error = "Invalid or expired verification code" });
        }

        // Find user and generate JWT tokens
        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.PhoneNumber == request.PhoneNumber);

        if (user == null || !user.IsActive)
        {
            return BadRequest(new { error = "Invalid or expired verification code" });
        }

        // Generate JWT tokens using the same flow as password login. If the account has
        // email 2FA enabled, this suspends the login and emails a second-factor code
        // instead of signing the user in immediately (see AuthResult.RequiresTwoFactor) —
        // an SMS OTP only proves phone possession, not the second factor.
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

        Response.Cookies.Append("refreshToken", loginResult.RefreshToken!, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(7)
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

public record EmailOtpRequest(string Email);
public record EmailOtpVerifyRequest(string Email, string Code);
public record SmsOtpRequest(string PhoneNumber);
public record SmsOtpVerifyRequest(string PhoneNumber, string Code);
