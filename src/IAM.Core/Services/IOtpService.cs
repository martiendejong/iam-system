using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IOtpService
{
    Task<bool> SendEmailOtpAsync(string email, OtpPurpose purpose);
    Task<bool> SendSmsOtpAsync(string phoneNumber, OtpPurpose purpose);
    Task<bool> ValidateOtpAsync(string? email, string? phoneNumber, string code, OtpPurpose purpose);
    Task<int> CleanupExpiredCodesAsync();
    Task<bool> SendLoginTwoFactorCodeAsync(User user, string? returnUrl = null);
}
