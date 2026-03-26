namespace IAM.Core.Services;

public interface IEmailService
{
    Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default);
    Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default);
    Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default);
    Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default);
    Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default);
    Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default);
    Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default);
    Task SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default);
    Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default);
}
