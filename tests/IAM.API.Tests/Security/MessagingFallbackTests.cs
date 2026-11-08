using IAM.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5576: with sending disabled, the e-mail / SMS fallback must never write reset links,
/// tokens or login codes to the log, and outside Development it must fail closed.
/// </summary>
public class MessagingFallbackTests
{
    private const string Token = "SECRET-RESET-TOKEN-9f3a1c";
    private const string Code = "482913";

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception != null) Lines.Add(exception.ToString());
        }
    }

    private static EmailService NewEmail(EmailSettings s, CaptureLogger<EmailService> log) =>
        new(Options.Create(s), null!, log);

    private static SmsService NewSms(SmsSettings s, CaptureLogger<SmsService> log) =>
        new(null!, Options.Create(s), log);

    [Theory]
    [InlineData(true, false)]  // Development
    [InlineData(false, true)]  // explicit opt-out
    public async Task Email_fallback_logs_no_token_or_link(bool dev, bool optOut)
    {
        var log = new CaptureLogger<EmailService>();
        var svc = NewEmail(new EmailSettings { IsDevelopmentEnvironment = dev, AllowDisabledSending = optOut,
            BaseUrl = "https://iam.example.com" }, log);

        await svc.SendPasswordResetAsync("a@example.com", "alice", Token);

        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains(Token) || l.Contains("reset"));
        Assert.Contains(log.Lines, l => l.Contains("a@example.com"));
    }

    [Fact]
    public async Task Email_login_code_never_logged()
    {
        var log = new CaptureLogger<EmailService>();
        var svc = NewEmail(new EmailSettings { IsDevelopmentEnvironment = true }, log);

        await svc.SendMfaCodeAsync("a@example.com", "alice", Code);

        Assert.DoesNotContain(log.Lines, l => l.Contains(Code));
    }

    [Fact]
    public async Task Email_outside_development_fails_closed_without_body()
    {
        var log = new CaptureLogger<EmailService>();
        var svc = NewEmail(new EmailSettings(), log);

        var ok = await svc.SendRawEmailAsync("a@example.com", "Subject", $"<p>{Token}</p>");

        Assert.False(ok);
        Assert.DoesNotContain(log.Lines, l => l.Contains(Token));
    }

    [Fact]
    public async Task Sms_fallback_in_development_logs_no_code()
    {
        var log = new CaptureLogger<SmsService>();
        var svc = NewSms(new SmsSettings { IsDevelopmentEnvironment = true }, log);

        var ok = await svc.SendSmsAsync("+31600000000", $"Your code is {Code}");

        Assert.True(ok);
        Assert.DoesNotContain(log.Lines, l => l.Contains(Code));
        Assert.Contains(log.Lines, l => l.Contains("+31600000000"));
    }

    [Fact]
    public async Task Sms_outside_development_fails_closed_without_body()
    {
        var log = new CaptureLogger<SmsService>();
        var svc = NewSms(new SmsSettings(), log);

        var ok = await svc.SendSmsAsync("+31600000000", $"Your code is {Code}");

        Assert.False(ok);
        Assert.DoesNotContain(log.Lines, l => l.Contains(Code));
    }

    [Fact]
    public void Startup_in_production_without_email_settings_throws_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MessagingStartupCheck.Validate(new EmailSettings(), new SmsSettings(), new CaptureLogger<object>()));
        Assert.Contains("Email:EnableSending", ex.Message);
    }

    [Fact]
    public void Startup_allows_enabled_email_development_and_explicit_opt_out()
    {
        var log = new CaptureLogger<object>();
        MessagingStartupCheck.Validate(new EmailSettings { EnableSending = true }, new SmsSettings(), log);
        MessagingStartupCheck.Validate(new EmailSettings { IsDevelopmentEnvironment = true }, new SmsSettings(), log);
        MessagingStartupCheck.Validate(new EmailSettings { AllowDisabledSending = true }, new SmsSettings(), log);
    }

    [Fact]
    public void Startup_with_sms_disabled_warns_but_does_not_stop_the_host()
    {
        var log = new CaptureLogger<object>();
        MessagingStartupCheck.Validate(new EmailSettings { EnableSending = true }, new SmsSettings(), log);
        Assert.Contains(log.Lines, l => l.Contains("SMS sending is disabled"));
    }
}
