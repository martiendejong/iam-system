using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(string email, string password, string firstName, string lastName);
    Task<AuthResult> LoginAsync(string email, string password, string? ipAddress = null, string? userAgent = null, string? returnUrl = null);
    Task<AuthResult> RefreshTokenAsync(string refreshToken, string? ipAddress = null, string? userAgent = null);
    Task<bool> RevokeTokenAsync(string refreshToken);
    Task<bool> VerifyEmailAsync(string token);
    Task<bool> SendPasswordResetAsync(string email);
    Task<bool> ResetPasswordAsync(string token, string newPassword);
    Task<AuthResult> LoginBypassPasswordAsync(User user, string? ipAddress = null, string? userAgent = null);
    Task<bool> ResendVerificationEmailAsync(Guid userId);

    /// <summary>
    /// Completes a login that was suspended for adaptive-MFA step-up verification
    /// (see AuthResult.RequiresStepUp), by validating the emailed OTP and issuing tokens.
    /// </summary>
    Task<AuthResult> VerifyStepUpAsync(string email, string code, string? ipAddress = null, string? userAgent = null);
    Task<AuthResult> VerifyLoginTwoFactorAsync(Guid userId, string code, string? ipAddress = null, string? userAgent = null);
    Task<bool> ResendLoginTwoFactorCodeAsync(Guid userId, string? returnUrl = null);
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
    /// True when the risk assessment for this login required an additional step-up
    /// verification (adaptive MFA). No tokens are issued yet; the caller must complete
    /// the challenge via VerifyStepUpAsync.
    /// </summary>
    public bool RequiresStepUp { get; set; }
    public bool RequiresTwoFactor { get; set; }
}
