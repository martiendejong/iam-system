using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MfaController : ControllerBase
{
    private readonly ITotpService _totpService;
    private readonly IAMDbContext _context;

    public MfaController(ITotpService totpService, IAMDbContext context)
    {
        _totpService = totpService;
        _context = context;
    }

    /// <summary>
    /// Start TOTP setup. Returns the secret and QR code URI for authenticator app enrollment.
    /// The user must verify a code via /totp/verify before TOTP is activated.
    /// </summary>
    [HttpPost("totp/setup")]
    public async Task<IActionResult> SetupTotp(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        try
        {
            var result = await _totpService.EnableTotpAsync(userId.Value, ct);

            return Ok(new
            {
                secret = result.Secret,
                qrCodeUri = result.QrCodeUri,
                manualEntryKey = result.ManualEntryKey
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Verify a TOTP code to activate two-factor authentication.
    /// Must be called after /totp/setup with a valid code from the authenticator app.
    /// </summary>
    [HttpPost("totp/verify")]
    public async Task<IActionResult> VerifyTotp([FromBody] TotpCodeRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var success = await _totpService.VerifyAndActivateTotpAsync(userId.Value, request.Code, ct);

        if (!success)
        {
            return BadRequest(new { error = "Invalid verification code. Ensure your authenticator app is synced and try again." });
        }

        // Generate initial set of recovery codes upon activation
        var recoveryCodes = await _totpService.GenerateRecoveryCodesAsync(userId.Value, 8, ct);

        return Ok(new
        {
            message = "TOTP has been enabled successfully.",
            recoveryCodes,
            recoveryCodeWarning = "Save these recovery codes in a safe place. They will not be shown again. Each code can only be used once."
        });
    }

    /// <summary>
    /// Disable TOTP for the current user. Requires a valid TOTP code for confirmation.
    /// </summary>
    [HttpPost("totp/disable")]
    public async Task<IActionResult> DisableTotp([FromBody] TotpCodeRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var success = await _totpService.DisableTotpAsync(userId.Value, request.Code, ct);

        if (!success)
        {
            return BadRequest(new { error = "Invalid code. TOTP was not disabled." });
        }

        return Ok(new { message = "TOTP has been disabled." });
    }

    /// <summary>
    /// Validate a TOTP code during the login flow (second factor).
    /// Called after password authentication when TwoFactorEnabled is true.
    /// </summary>
    [HttpPost("totp/validate")]
    [AllowAnonymous]
    public async Task<IActionResult> ValidateTotp([FromBody] TotpValidateRequest request, CancellationToken ct)
    {
        // Try TOTP code first
        var success = await _totpService.ValidateTotpLoginAsync(request.UserId, request.Code, ct);

        if (!success)
        {
            // Fall back to recovery code
            success = await _totpService.UseRecoveryCodeAsync(request.UserId, request.Code, ct);
        }

        if (!success)
        {
            return BadRequest(new { error = "Invalid authentication code." });
        }

        return Ok(new
        {
            message = "Two-factor authentication verified.",
            verified = true
        });
    }

    /// <summary>
    /// Enable email-based two-factor authentication for the current user. Requires a verified email address.
    /// </summary>
    [HttpPost("email/enable")]
    public async Task<IActionResult> EnableEmailMfa(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var user = await _context.Users.FindAsync(new object[] { userId.Value }, ct);
        if (user == null)
            return NotFound(new { error = "User not found" });

        if (!user.EmailConfirmed)
            return BadRequest(new { error = "Verify your email address before enabling email two-factor authentication." });

        if (user.TwoFactorEnabled)
            return BadRequest(new { error = "Two-factor authentication is already enabled." });

        user.TwoFactorEnabled = true;
        user.TwoFactorMethod = TwoFactorMethod.Email;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return Ok(new { message = "Email-based two-factor authentication has been enabled.", method = "email" });
    }

    /// <summary>
    /// Disable email-based two-factor authentication for the current user.
    /// </summary>
    [HttpPost("email/disable")]
    public async Task<IActionResult> DisableEmailMfa(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var user = await _context.Users.FindAsync(new object[] { userId.Value }, ct);
        if (user == null)
            return NotFound(new { error = "User not found" });

        if (!user.TwoFactorEnabled || user.TwoFactorMethod != TwoFactorMethod.Email)
            return BadRequest(new { error = "Email two-factor authentication is not enabled." });

        user.TwoFactorEnabled = false;
        user.TwoFactorMethod = TwoFactorMethod.None;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return Ok(new { message = "Email-based two-factor authentication has been disabled." });
    }

    /// <summary>
    /// Generate a new set of recovery codes. Replaces any existing recovery codes.
    /// Requires an active TOTP enrollment.
    /// </summary>
    [HttpPost("recovery-codes")]
    public async Task<IActionResult> GenerateRecoveryCodes(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        try
        {
            var codes = await _totpService.GenerateRecoveryCodesAsync(userId.Value, 8, ct);

            return Ok(new
            {
                recoveryCodes = codes,
                warning = "Save these codes in a safe place. Your previous recovery codes have been invalidated. Each code can only be used once."
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get MFA status for the current user: whether TOTP is enabled, the method, and remaining recovery codes.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetMfaStatus(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var user = await _context.Users.FindAsync(new object[] { userId.Value }, ct);
        if (user == null)
            return NotFound(new { error = "User not found" });

        var remainingRecoveryCodes = await _context.RecoveryCodes
            .CountAsync(rc => rc.UserId == userId.Value && !rc.IsUsed, ct);

        return Ok(new
        {
            twoFactorEnabled = user.TwoFactorEnabled,
            method = user.TwoFactorEnabled ? user.TwoFactorMethod.ToString().ToLowerInvariant() : (string?)null,
            recoveryCodesRemaining = remainingRecoveryCodes,
            hasPendingSetup = !user.TwoFactorEnabled && !string.IsNullOrWhiteSpace(user.TwoFactorSecret)
        });
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(claim) || !Guid.TryParse(claim, out var userId))
            return null;
        return userId;
    }
}

public record TotpCodeRequest(string Code);
public record TotpValidateRequest(Guid UserId, string Code);
