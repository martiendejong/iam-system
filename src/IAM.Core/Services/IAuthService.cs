using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(string email, string password, string firstName, string lastName);
    /// <summary>
    /// Password sign-in. An unknown email, a wrong password and a wrong password on a locked account
    /// all return the same "Invalid email or password" after the same work; repeated failures from one
    /// caller are refused with AuthResult.RetryAfterSeconds set (see ILoginThrottle) instead of locking
    /// the account. A 2FA lock is only reported once the password is correct.
    /// </summary>
    Task<AuthResult> LoginAsync(string email, string password, string? ipAddress = null, string? userAgent = null, string? returnUrl = null, bool rememberMe = false);
    Task<AuthResult> RefreshTokenAsync(string refreshToken, string? ipAddress = null, string? userAgent = null);
    Task<bool> RevokeTokenAsync(string refreshToken);
    Task<bool> VerifyEmailAsync(string token);
    Task<bool> SendPasswordResetAsync(string email);
    Task<bool> ResetPasswordAsync(string token, string newPassword);
    /// <summary>
    /// Issues tokens for an already-authenticated user with NO second-factor check. Only call this once
    /// the 2FA gate has been passed (the verify-code methods below do). A sign-in entry point (passkey,
    /// social, magic link, OTP) must call CompletePasswordlessLoginAsync instead, never this.
    /// </summary>
    Task<AuthResult> LoginBypassPasswordAsync(User user, string? ipAddress = null, string? userAgent = null, bool rememberMe = false);
    Task<bool> ResendVerificationEmailAsync(Guid userId);

    /// <summary>
    /// Completes a login that was suspended for adaptive-MFA step-up verification
    /// (see AuthResult.RequiresStepUp), by validating the emailed OTP and issuing tokens.
    /// </summary>
    Task<AuthResult> VerifyStepUpAsync(string email, string code, string? ipAddress = null, string? userAgent = null, bool rememberMe = false);
    Task<AuthResult> VerifyLoginTwoFactorAsync(Guid userId, string code, string? ipAddress = null, string? userAgent = null, bool rememberMe = false);
    Task<bool> ResendLoginTwoFactorCodeAsync(Guid userId, string? returnUrl = null);

    /// <summary>
    /// Completes a login for a user who has already proven an alternate primary factor
    /// (e.g. clicking a magic link). If the account has email 2FA enabled, this suspends
    /// the login and emails a second-factor code instead of issuing tokens immediately —
    /// the caller must complete it via VerifyLoginTwoFactorAsync, exactly like the
    /// password + email-2FA flow (see AuthResult.RequiresTwoFactor). This mirrors the
    /// 2FA gate in LoginAsync so no alternate login path can bypass it.
    /// </summary>
    Task<AuthResult> CompletePasswordlessLoginAsync(User user, string? ipAddress = null, string? userAgent = null, string? returnUrl = null, bool rememberMe = false);
}

public class AuthResult
{
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public User? User { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string[]>? ValidationErrors { get; set; }

    /// <summary>
    /// Set (to whole seconds, at least 1) when a password sign-in was refused because this
    /// caller has to wait first (task 5166). The caller answers 429 with a Retry-After header;
    /// it says nothing about whether the account exists.
    /// </summary>
    public int RetryAfterSeconds { get; set; }

    /// <summary>
    /// True when the risk assessment for this login required an additional step-up
    /// verification (adaptive MFA). No tokens are issued yet; the caller must complete
    /// the challenge via VerifyStepUpAsync.
    /// </summary>
    public bool RequiresStepUp { get; set; }
    public bool RequiresTwoFactor { get; set; }

    /// <summary>
    /// Access token lifetime (minutes) actually used to issue <see cref="AccessToken"/>,
    /// resolved from the user's organization Token Configuration when one exists,
    /// otherwise today's default. Unset (0) when no tokens were issued.
    /// </summary>
    public int AccessTokenLifetimeMinutes { get; set; }

    /// <summary>
    /// Refresh token / session cookie lifetime (days) actually used to issue
    /// <see cref="RefreshToken"/>, resolved the same way as <see cref="AccessTokenLifetimeMinutes"/>.
    /// Callers that set a refresh-token cookie must derive its Expires from this value
    /// rather than a hardcoded literal, so the cookie matches the token it carries.
    /// </summary>
    public int RefreshTokenLifetimeDays { get; set; }
}
