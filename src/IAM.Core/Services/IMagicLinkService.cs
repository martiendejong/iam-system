using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IMagicLinkService
{
    Task<bool> SendMagicLinkAsync(string email, MagicLinkPurpose purpose, string? ipAddress = null, string? returnUrl = null);
    Task<User?> ValidateMagicLinkAsync(string token);
    Task<int> CleanupExpiredTokensAsync();
}
