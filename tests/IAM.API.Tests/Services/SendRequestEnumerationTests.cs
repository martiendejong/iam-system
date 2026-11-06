using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.API.Tests.Services;

/// <summary>
/// The e-mail code, SMS code and magic-link request routes must answer identically for unknown,
/// inactive and active addresses, including after the per-address limit is exceeded (task 5567).
/// </summary>
public class SendRequestEnumerationTests
{
    private const string Unknown = "nobody@example.com";
    private const string Inactive = "inactive@example.com";
    private const string Active = "active@example.com";
    private const string UnknownPhone = "+31600000000";
    private const string InactivePhone = "+31600000001";
    private const string ActivePhone = "+31600000002";

    private static async Task<(IAMDbContext ctx, CountingEmail email, CountingSms sms, OtpService otp, MagicLinkService magic)> Setup()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var ctx = new IAMDbContext(options);
        ctx.Users.Add(NewUser(Inactive, InactivePhone, false));
        ctx.Users.Add(NewUser(Active, ActivePhone, true));
        await ctx.SaveChangesAsync();

        var email = new CountingEmail();
        var sms = new CountingSms();
        var settings = Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com/auth" });
        var throttle = new InMemoryRequestThrottle();
        var otp = new OtpService(ctx, email, sms, settings, NullLogger<OtpService>.Instance, throttle);
        var magic = new MagicLinkService(ctx, email, settings, NullLogger<MagicLinkService>.Instance, throttle);
        return (ctx, email, sms, otp, magic);
    }

    private static User NewUser(string address, string phone, bool active) => new()
    {
        Email = address,
        PhoneNumber = phone,
        PasswordHash = "x",
        FirstName = "T",
        LastName = "U",
        EmailConfirmed = true,
        IsActive = active
    };

    [Fact]
    public async Task EmailOtp_TwentyRequests_AllReturnTrueForEveryAddressType_AndMailIsCapped()
    {
        var (_, email, _, otp, _) = await Setup();
        foreach (var address in new[] { Unknown, Inactive, Active })
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.True(await otp.SendEmailOtpAsync(address, OtpPurpose.Login, $"10.1.0.{Array.IndexOf(new[] { Unknown, Inactive, Active }, address)}"));
            }
        }

        Assert.Equal(10, email.Sent.Count(a => a == Active)); // capped at the per-address limit
        Assert.Equal(0, email.Sent.Count(a => a == Unknown));
        Assert.Equal(0, email.Sent.Count(a => a == Inactive));
    }

    [Fact]
    public async Task EmailOtp_LimitCountsUnknownAddressesToo()
    {
        var (_, email, _, otp, _) = await Setup();
        // 10 hits on an unknown address exhaust its bucket; later behaviour is unchanged (still true, still no mail).
        for (var i = 0; i < 25; i++) Assert.True(await otp.SendEmailOtpAsync(Unknown, OtpPurpose.Login));
        Assert.Empty(email.Sent);

        // Case differences must not give a fresh bucket.
        for (var i = 0; i < 10; i++) await otp.SendEmailOtpAsync(Active, OtpPurpose.Login);
        await otp.SendEmailOtpAsync(Active.ToUpperInvariant(), OtpPurpose.Login);
        Assert.Equal(10, email.Sent.Count);
    }

    [Fact]
    public async Task SmsOtp_TwentyRequests_AllReturnTrueForEveryAddressType_AndSmsIsCapped()
    {
        var (_, _, sms, otp, _) = await Setup();
        foreach (var phone in new[] { UnknownPhone, InactivePhone, ActivePhone })
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.True(await otp.SendSmsOtpAsync(phone, OtpPurpose.Login, $"10.2.0.{Array.IndexOf(new[] { UnknownPhone, InactivePhone, ActivePhone }, phone)}"));
            }
        }

        Assert.Equal(10, sms.Sent.Count(p => p == ActivePhone));
        Assert.Equal(0, sms.Sent.Count(p => p == UnknownPhone));
        Assert.Equal(0, sms.Sent.Count(p => p == InactivePhone));
    }

    [Fact]
    public async Task MagicLink_TwentyRequests_AllReturnTrueForEveryAddressType_AndMailIsCapped()
    {
        var (_, email, _, _, magic) = await Setup();
        foreach (var address in new[] { Unknown, Inactive, Active })
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.True(await magic.SendMagicLinkAsync(address, MagicLinkPurpose.Login, $"10.3.0.{Array.IndexOf(new[] { Unknown, Inactive, Active }, address)}"));
            }
        }

        Assert.Equal(5, email.Sent.Count(a => a == Active));
        Assert.Equal(0, email.Sent.Count(a => a == Unknown));
        Assert.Equal(0, email.Sent.Count(a => a == Inactive));
    }

    [Fact]
    public async Task PerIpLimit_CountsRequestsForUnknownAddresses_AndStopsSendsToRealUsers()
    {
        var (_, email, _, _, magic) = await Setup();
        // 30 requests from one IP, each to a different unknown address, exhaust the IP bucket.
        for (var i = 0; i < 30; i++) Assert.True(await magic.SendMagicLinkAsync($"u{i}@example.com", MagicLinkPurpose.Login, "10.0.0.9"));

        // A real user from that same IP is now silently skipped, with the same return value.
        Assert.True(await magic.SendMagicLinkAsync(Active, MagicLinkPurpose.Login, "10.0.0.9"));
        Assert.Empty(email.Sent);

        // A different IP is unaffected.
        Assert.True(await magic.SendMagicLinkAsync(Active, MagicLinkPurpose.Login, "10.0.0.10"));
        Assert.Single(email.Sent);
    }

    private sealed class CountingSms : ISmsService
    {
        public List<string> Sent { get; } = new();
        public Task<bool> SendSmsAsync(string phoneNumber, string message)
        {
            Sent.Add(phoneNumber);
            return Task.FromResult(true);
        }
    }

    private sealed class CountingEmail : IEmailService
    {
        public List<string> Sent { get; } = new();

        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default)
        {
            Sent.Add(email);
            return Task.CompletedTask;
        }

        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task<bool> SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.FromResult(true);
        public Task<bool> SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
    }
}
