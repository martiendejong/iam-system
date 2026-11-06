using System.Net;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Auth;

/// <summary>
/// Task 5488: proof the caller is the account owner, not just someone holding an access token. Required before
/// the account's second-factor material changes (disable e-mail 2FA, regenerate recovery codes, register a
/// passkey). Accepts the current password (same lockout rule as the portal password change and the login) or a
/// one-time code e-mailed to the verified address (for social/SSO sign-ups that never knew a password).
/// Single place for the rule: controllers call <see cref="RequireAsync"/>, then <see cref="RecordSuccessAsync"/>.
/// </summary>
public class SensitiveActionGuard
{
    public const string ConfirmationRequiredCode = "confirmation_required";

    private readonly IAMDbContext _context;
    private readonly IOtpService _otpService;
    private readonly IEmailService _emailService;
    private readonly ILogger<SensitiveActionGuard> _logger;

    public SensitiveActionGuard(
        IAMDbContext context,
        IOtpService otpService,
        IEmailService emailService,
        ILogger<SensitiveActionGuard> logger)
    {
        _context = context;
        _otpService = otpService;
        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>
    /// Returns null when the confirmation is valid; otherwise the 4xx result to return (nothing must be changed).
    /// A code, when supplied, wins over a password.
    /// </summary>
    public async Task<IActionResult?> RequireAsync(User user, string? currentPassword, string? emailCode, CancellationToken ct)
    {
        if (LoginLockout.IsLocked(user))
            return Error(HttpStatusCode.Locked, $"Account locked. Try again after {user.LockoutEnd:yyyy-MM-dd HH:mm:ss} UTC", "account_locked");

        if (!string.IsNullOrWhiteSpace(emailCode))
        {
            if (!user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email))
                return Error(HttpStatusCode.Forbidden, "Confirmation code is not available: the e-mail address is not verified.", ConfirmationRequiredCode);

            var valid = await _otpService.ValidateOtpAsync(user.Email, null, emailCode.Trim(), OtpPurpose.SensitiveAction);
            return valid
                ? null
                : Error(HttpStatusCode.Forbidden, "The confirmation code is invalid or expired.", ConfirmationRequiredCode);
        }

        if (string.IsNullOrEmpty(currentPassword))
            return Error(HttpStatusCode.Forbidden, "Confirm with your current password or an e-mailed code.", ConfirmationRequiredCode);

        // Hash compare in a try: a malformed stored hash must fail closed, not 500 past the lockout counter.
        var ok = false;
        try { ok = BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash); }
        catch (Exception ex) { _logger.LogWarning(ex, "Password hash check failed for user {UserId}", user.Id); }

        if (!ok)
        {
            LoginLockout.RegisterFailure(user);
            await _context.SaveChangesAsync(ct);
            return Error(HttpStatusCode.Forbidden, "The password is incorrect.", ConfirmationRequiredCode);
        }

        if (user.FailedLoginAttempts != 0 || user.IsLockedOut)
        {
            LoginLockout.Reset(user);
            await _context.SaveChangesAsync(ct);
        }
        return null;
    }

    /// <summary>Sends the one-time confirmation code to the user's verified address.</summary>
    public async Task<IActionResult> SendCodeAsync(User user)
    {
        if (!user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email))
            return Error(HttpStatusCode.BadRequest, "Verify your email address to receive a confirmation code.", "email_not_verified");

        var sent = await _otpService.SendEmailOtpAsync(user.Email, OtpPurpose.SensitiveAction);
        return sent
            ? new OkObjectResult(new { message = "A confirmation code has been sent to your email address." })
            : Error(HttpStatusCode.TooManyRequests, "Too many codes requested. Try again later.", "rate_limited");
    }

    /// <summary>Audit entry plus an e-mail notice to the owner after a confirmed change went through.</summary>
    public async Task RecordSuccessAsync(User user, string auditAction, string description, HttpContext http, CancellationToken ct)
    {
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = user.Id,
            Action = auditAction,
            Resource = "User",
            Details = """{"source":"api","confirmed":true}""",
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = http.Request.Headers["User-Agent"].ToString()
        });
        await _context.SaveChangesAsync(ct);

        try
        {
            var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(user.FirstName) ? "there" : user.FirstName);
            var ip = WebUtility.HtmlEncode(http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            var body = $"<p>Hi {name},</p><p>{WebUtility.HtmlEncode(description)}</p>" +
                       $"<p><strong>Time:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC<br/><strong>IP address:</strong> {ip}</p>" +
                       "<p>If this was not you, reset your password immediately and contact your administrator.</p>";
            await _emailService.SendRawEmailAsync(user.Email, "Security change on your account", body, ct);
        }
        catch (Exception ex)
        {
            // The change is committed and audited; a mail failure must not turn it into an error response.
            _logger.LogWarning(ex, "Security notice e-mail failed for user {UserId}", user.Id);
        }
    }

    private static IActionResult Error(HttpStatusCode status, string message, string code) =>
        new ObjectResult(new { error = message, code }) { StatusCode = (int)status };
}

/// <summary>Confirmation sent with a sensitive request: current password or e-mailed code.</summary>
public record SensitiveActionRequest(string? CurrentPassword = null, string? EmailCode = null);
