using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IOtpService
{
    Task<bool> SendEmailOtpAsync(string email, OtpPurpose purpose, string? ipAddress = null);
    Task<bool> SendSmsOtpAsync(string phoneNumber, OtpPurpose purpose, string? ipAddress = null);
    Task<bool> ValidateOtpAsync(string? email, string? phoneNumber, string code, OtpPurpose purpose);
    Task<int> CleanupExpiredCodesAsync();
    Task<bool> SendLoginTwoFactorCodeAsync(User user, string? returnUrl = null);
}
