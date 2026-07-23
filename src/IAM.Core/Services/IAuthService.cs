using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(string email, string password, string firstName, string lastName);
    Task<AuthResult> LoginAsync(string email, string password, string? ipAddress = null, string? userAgent = null);
    Task<AuthResult> RefreshTokenAsync(string refreshToken, string? ipAddress = null, string? userAgent = null);
    Task<bool> RevokeTokenAsync(string refreshToken);
    Task<bool> VerifyEmailAsync(string token);
    Task<bool> SendPasswordResetAsync(string email);
    Task<bool> ResetPasswordAsync(string token, string newPassword);
    Task<AuthResult> LoginBypassPasswordAsync(User user, string? ipAddress = null, string? userAgent = null);
    Task<bool> ResendVerificationEmailAsync(Guid userId);
}

public class AuthResult
{
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public User? User { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string[]>? ValidationErrors { get; set; }
}
