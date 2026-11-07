using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IOtpService
{
    Task<bool> SendEmailOtpAsync(string email, OtpPurpose purpose, string? ipAddress = null);
    Task<bool> SendSmsOtpAsync(string phoneNumber, OtpPurpose purpose, string? ipAddress = null);
    Task<bool> ValidateOtpAsync(string? email, string? phoneNumber, string code, OtpPurpose purpose);
    /// <summary>Sends a code to the user's current profile number; the number only counts for login once confirmed.</summary>
    Task<PhoneVerificationSendResult> SendPhoneVerificationAsync(User user, string? ipAddress = null);
    Task<PhoneVerificationConfirmResult> ConfirmPhoneVerificationAsync(User user, string code);
    Task<int> CleanupExpiredCodesAsync();
    Task<bool> SendLoginTwoFactorCodeAsync(User user, string? returnUrl = null);
}

public enum PhoneVerificationSendResult { Sent, InvalidNumber, AlreadyVerifiedElsewhere, RateLimited }
public enum PhoneVerificationConfirmResult { Verified, InvalidNumber, InvalidCode, AlreadyVerifiedElsewhere }
