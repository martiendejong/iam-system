using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IOtpService
{
    Task<bool> SendEmailOtpAsync(string email, OtpPurpose purpose);
    Task<bool> SendSmsOtpAsync(string phoneNumber, OtpPurpose purpose);
    Task<bool> ValidateOtpAsync(string? email, string? phoneNumber, string code, OtpPurpose purpose);
    Task<int> CleanupExpiredCodesAsync();
    Task<bool> SendLoginTwoFactorCodeAsync(User user, string? returnUrl = null);
    /// <summary>
    /// Issues a short-lived single-use challenge token that proves the password step completed.
    /// Must be validated by VerifyLoginTotpAsync before accepting an authenticator code.
    /// </summary>
    Task<string> GenerateLoginTotpChallengeAsync(User user);
}
