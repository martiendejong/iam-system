using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.API.Tests.Services;

/// <summary>
/// Builds the real <see cref="AuthService"/> (the shared 2FA gate + token issuer) on top of a test
/// context, with an e-mail double that records the second-factor codes it is asked to send.
/// SocialAuthService delegates sign-in to it (task 4573), so every test that constructs a
/// SocialAuthService needs one; this keeps AuthService's constructor wiring in a single place.
/// </summary>
internal static class AuthServiceTestFactory
{
    public static AuthService Create(
        IAMDbContext context,
        IConfiguration configuration,
        RecordingEmailService? emailService = null,
        ILoginThrottle? loginThrottle = null)
    {
        emailService ??= new RecordingEmailService();
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" });
        var otpService = new OtpService(context, emailService, new NoopSmsService(), emailSettings, NullLogger<OtpService>.Instance);
        var riskAssessmentService = new RiskAssessmentService(context, NullLogger<RiskAssessmentService>.Instance);
        var claimsMappingService = new ClaimsMappingService(context);

        return new AuthService(
            context, configuration, emailService, riskAssessmentService, otpService, claimsMappingService,
            NullLogger<AuthService>.Instance, loginThrottle ?? CreateLoginThrottle());
    }

    /// <summary>
    /// A real <see cref="LoginThrottle"/> on a private cache (so tests never share state), with the
    /// production defaults unless a configuration or clock is given.
    /// </summary>
    public static LoginThrottle CreateLoginThrottle(IConfiguration? configuration = null, TimeProvider? timeProvider = null)
    {
        return new LoginThrottle(
            new MemoryCache(new MemoryCacheOptions()),
            configuration ?? new ConfigurationBuilder().Build(),
            NullLogger<LoginThrottle>.Instance,
            timeProvider);
    }
}

/// <summary>Clock the tests move by hand, to step through the throttle's delays without sleeping.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>E-mail double that records the login second-factor codes sent through it.</summary>
internal sealed class RecordingEmailService : IEmailService
{
    public List<(string Email, string Code)> SentTwoFactorCodes { get; } = new();

    public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) => Task.CompletedTask;

    public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default)
    {
        SentTwoFactorCodes.Add((email, code));
        return Task.CompletedTask;
    }

    public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
    public Task SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
    public Task SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
}

internal sealed class NoopSmsService : ISmsService
{
    public Task<bool> SendSmsAsync(string phoneNumber, string message) => Task.FromResult(true);
}
