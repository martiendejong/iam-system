using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.API.Tests.Services;

public class MagicLinkServiceTests
{
    private static IAMDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new IAMDbContext(options);
    }

    private static (MagicLinkService service, FakeEmailService emailService, IAMDbContext context) CreateService()
    {
        var context = CreateContext();
        var emailService = new FakeEmailService();
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" });
        var service = new MagicLinkService(context, emailService, emailSettings, NullLogger<MagicLinkService>.Instance);
        return (service, emailService, context);
    }

    private static User CreateUser()
    {
        return new User
        {
            Email = "user@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            FirstName = "Test",
            LastName = "User",
            EmailConfirmed = true,
            IsActive = true
        };
    }

    [Fact]
    public async Task SendMagicLinkAsync_WithReturnUrl_IncludesItInTheEmailedLink()
    {
        var (service, emailService, context) = CreateService();
        var user = CreateUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await service.SendMagicLinkAsync(user.Email, MagicLinkPurpose.Login, returnUrl: "/connect/authorize?client_id=jengo-agi");

        Assert.True(result);
        Assert.Single(emailService.SentMagicLinkUrls);
        Assert.Contains("returnUrl=%2Fconnect%2Fauthorize%3Fclient_id%3Djengo-agi", emailService.SentMagicLinkUrls[0]);
        Assert.Contains("/auth/magic-link?token=", emailService.SentMagicLinkUrls[0]);
    }

    [Fact]
    public async Task SendMagicLinkAsync_WithoutReturnUrl_OmitsItFromTheEmailedLink()
    {
        var (service, emailService, context) = CreateService();
        var user = CreateUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await service.SendMagicLinkAsync(user.Email, MagicLinkPurpose.Login);

        Assert.True(result);
        Assert.Single(emailService.SentMagicLinkUrls);
        Assert.DoesNotContain("returnUrl=", emailService.SentMagicLinkUrls[0]);
    }

    [Fact]
    public async Task ValidateMagicLinkAsync_WithValidToken_ReturnsUserAndMarksTokenUsed()
    {
        var (service, _, context) = CreateService();
        var user = CreateUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        await service.SendMagicLinkAsync(user.Email, MagicLinkPurpose.Login);
        var token = await context.MagicLinkTokens.SingleAsync();

        var validated = await service.ValidateMagicLinkAsync(token.Token);
        var reusedResult = await service.ValidateMagicLinkAsync(token.Token);

        Assert.NotNull(validated);
        Assert.Equal(user.Id, validated!.Id);
        Assert.Null(reusedResult);
    }

    [Fact]
    public async Task ValidateMagicLinkAsync_WithUnknownToken_ReturnsNull()
    {
        var (service, _, _) = CreateService();

        var result = await service.ValidateMagicLinkAsync("does-not-exist");

        Assert.Null(result);
    }

    private class FakeEmailService : IEmailService
    {
        public List<string> SentMagicLinkUrls { get; } = new();

        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default)
        {
            SentMagicLinkUrls.Add(code);
            return Task.CompletedTask;
        }

        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
    }
}
