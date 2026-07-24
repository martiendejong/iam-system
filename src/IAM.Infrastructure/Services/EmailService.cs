using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using IAM.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IAM.Infrastructure.Services;

public partial class EmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ITenantBrandingService _brandingService;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<EmailSettings> settings, ITenantBrandingService brandingService, ILogger<EmailService> logger)
    {
        _settings = settings.Value;
        _brandingService = brandingService;
        _logger = logger;
    }

    private async Task<(string? logoUrl, string? headerHtml, string? footerHtml, bool whiteLabel)> GetBrandingAsync(Guid? tenantId, CancellationToken ct)
    {
        if (tenantId == null)
            return (null, null, null, false);

        var branding = await _brandingService.GetByTenantIdAsync(tenantId.Value, ct);
        if (branding == null)
            return (null, null, null, false);

        return (branding.LogoUrl, branding.EmailHeaderHtml, branding.EmailFooterHtml, branding.WhiteLabelEnabled);
    }

    public async Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default)
    {
        var verifyUrl = $"{_settings.BaseUrl?.TrimEnd('/')}/verify-email?token={verificationToken}";

        var subject = "Verify your email address";
        var body = BuildEmailBody(
            greeting: $"Hi {EscapeHtml(username)},",
            mainMessage: "Thank you for registering. Please verify your email address to activate your account.",
            ctaUrl: verifyUrl,
            ctaText: "Verify Email Address",
            additionalInfo: "This verification link will expire in 24 hours. If you did not create an account, you can safely ignore this email.",
            footer: null
        );

        await SendEmailAsync(email, subject, body, ct);
    }

    public async Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default)
    {
        var resetUrl = $"{_settings.BaseUrl?.TrimEnd('/')}/reset-password?token={resetToken}";

        var subject = "Reset your password";
        var body = BuildEmailBody(
            greeting: $"Hi {EscapeHtml(username)},",
            mainMessage: "We received a request to reset the password for your account. Click the button below to set a new password.",
            ctaUrl: resetUrl,
            ctaText: "Reset Password",
            additionalInfo: "This link will expire in 1 hour. If you did not request a password reset, please ignore this email and your password will remain unchanged.",
            footer: "If you are concerned about your account security, please contact your administrator."
        );

        await SendEmailAsync(email, subject, body, ct);
    }

    public async Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default)
    {
        var subject = "Your verification code";
        var body = BuildEmailBody(
            greeting: $"Hi {EscapeHtml(username)},",
            mainMessage: "Your multi-factor authentication code is:",
            ctaUrl: null,
            ctaText: null,
            additionalInfo: "This code will expire in 5 minutes. If you did not request this code, please secure your account immediately.",
            footer: null,
            highlightCode: code
        );

        await SendEmailAsync(email, subject, body, ct);
    }

    public async Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default)
    {
        var subject = "Your sign-in verification code";
        var body = BuildEmailBody(
            greeting: $"Hi {EscapeHtml(username)},",
            mainMessage: "Enter this code to finish signing in, or click the button below to verify automatically.",
            ctaUrl: verifyUrl,
            ctaText: "Verify and Sign In",
            additionalInfo: "This code will expire in 10 minutes. If you did not attempt to sign in, please secure your account immediately.",
            footer: null,
            highlightCode: code
        );

        await SendEmailAsync(email, subject, body, ct);
    }

    public async Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default)
    {
        var subject = "New sign-in detected on your account";
        var body = BuildEmailBody(
            greeting: $"Hi {EscapeHtml(username)},",
            mainMessage: "A new sign-in was detected on your account. If this was you, no action is needed.",
            ctaUrl: null,
            ctaText: null,
            additionalInfo: $"<strong>Device:</strong> {EscapeHtml(deviceInfo)}<br/><strong>IP Address:</strong> {EscapeHtml(ipAddress)}<br/><strong>Time:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
            footer: "If you do not recognize this activity, please reset your password immediately and contact your administrator."
        );

        await SendEmailAsync(email, subject, body, ct);
    }

    public async Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default)
    {
        var subject = "New device provisioned";
        var body = BuildEmailBody(
            greeting: $"Hi {EscapeHtml(username)},",
            mainMessage: "A new device has been provisioned and linked to your tenant.",
            ctaUrl: null,
            ctaText: null,
            additionalInfo: $"<strong>Device Name:</strong> {EscapeHtml(deviceName)}<br/><strong>Device Type:</strong> {EscapeHtml(deviceType)}<br/><strong>Provisioned At:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
            footer: "If you did not authorize this device, please contact your administrator immediately."
        );

        await SendEmailAsync(email, subject, body, ct);
    }

    public async Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default)
    {
        var urgency = daysRemaining <= 7 ? "URGENT: " : "";
        var subject = $"{urgency}Certificate expiring for device {deviceName}";

        var urgencyColor = daysRemaining <= 7 ? "#d93025" : "#f9ab00";
        var body = BuildEmailBody(
            greeting: "Hello,",
            mainMessage: $"The certificate for device <strong>{EscapeHtml(deviceName)}</strong> is expiring soon.",
            ctaUrl: null,
            ctaText: null,
            additionalInfo: $"<div style=\"padding:12px 16px;background-color:{urgencyColor}15;border-left:4px solid {urgencyColor};border-radius:4px;margin:16px 0;\">"
                + $"<strong>Expiry Date:</strong> {expiryDate:yyyy-MM-dd HH:mm:ss} UTC<br/>"
                + $"<strong>Days Remaining:</strong> {daysRemaining}</div>"
                + "Please renew or rotate the certificate before it expires to avoid service disruption.",
            footer: null
        );

        await SendEmailAsync(email, subject, body, ct);
    }

    public async Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null)
    {
        var loginUrl = $"{_settings.BaseUrl?.TrimEnd('/')}/login";
        var branding = await GetBrandingAsync(tenantId, ct);

        var subject = $"Welcome to {tenantName}";
        var body = BuildEmailBody(
            greeting: $"Hi {EscapeHtml(username)},",
            mainMessage: $"Welcome to <strong>{EscapeHtml(tenantName)}</strong>! Your account has been set up and is ready to use.",
            ctaUrl: loginUrl,
            ctaText: "Sign In",
            additionalInfo: "If you have any questions, reach out to your organization administrator.",
            footer: null,
            logoUrl: branding.logoUrl,
            headerHtml: branding.headerHtml,
            footerHtml: branding.footerHtml,
            whiteLabel: branding.whiteLabel
        );

        await SendEmailAsync(email, subject, body, ct);
    }

    public async Task SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null)
    {
        var inviteUrl = $"{_settings.BaseUrl?.TrimEnd('/')}/accept-invite?token={inviteToken}";
        var branding = await GetBrandingAsync(tenantId, ct);

        var subject = $"{inviterName} invited you to join {tenantName}";
        var body = BuildEmailBody(
            greeting: "Hello,",
            mainMessage: $"<strong>{EscapeHtml(inviterName)}</strong> has invited you to join <strong>{EscapeHtml(tenantName)}</strong> on our platform. Click below to accept the invitation and set up your account.",
            ctaUrl: inviteUrl,
            ctaText: "Accept Invitation",
            additionalInfo: "This invitation link will expire in 7 days. If you were not expecting this invitation, you can safely ignore this email.",
            footer: null,
            logoUrl: branding.logoUrl,
            headerHtml: branding.headerHtml,
            footerHtml: branding.footerHtml,
            whiteLabel: branding.whiteLabel
        );

        await SendEmailAsync(email, subject, body, ct);
    }

    public async Task SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default)
    {
        await SendEmailAsync(email, subject, htmlBody, ct);
    }

    public async Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default)
    {
        try
        {
            // Basic format validation
            if (string.IsNullOrWhiteSpace(email))
                return false;

            if (!EmailFormatRegex().IsMatch(email))
                return false;

            // Extract domain
            var domain = email.Split('@')[1];

            // MX record lookup via DNS
            try
            {
                var hostEntry = await Dns.GetHostEntryAsync(domain, ct);
                // If DNS resolves at all, the domain exists
                // A proper MX check would require a DNS library for MX records,
                // but host resolution is a reasonable baseline check
                return hostEntry.AddressList.Length > 0;
            }
            catch (System.Net.Sockets.SocketException)
            {
                _logger.LogDebug("DNS lookup failed for domain {Domain} during email validation", domain);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email validation failed for {Email}", email);
            return false;
        }
    }

    // -- Private helpers --

    private async Task SendEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        if (!_settings.EnableSending)
        {
            _logger.LogInformation(
                "[EmailService DEV MODE] To: {To} | Subject: {Subject}\n{Body}",
                toEmail, subject, StripHtmlForLog(htmlBody));
            return;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_settings.FromEmail, _settings.FromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(new MailAddress(toEmail));

            using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                EnableSsl = _settings.UseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            if (!string.IsNullOrEmpty(_settings.SmtpUsername) && !string.IsNullOrEmpty(_settings.SmtpPassword))
            {
                client.Credentials = new NetworkCredential(_settings.SmtpUsername, _settings.SmtpPassword);
            }

            await client.SendMailAsync(message, ct);
            _logger.LogInformation("Email sent successfully to {To} with subject '{Subject}'", toEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send email to {To} with subject '{Subject}'", toEmail, subject);
            // Never throw - email failures should not break application flow
        }
    }

    private static string BuildEmailBody(
        string greeting,
        string mainMessage,
        string? ctaUrl,
        string? ctaText,
        string? additionalInfo,
        string? footer,
        string? highlightCode = null,
        string? logoUrl = null,
        string? headerHtml = null,
        string? footerHtml = null,
        bool whiteLabel = false)
    {
        var ctaSection = "";
        if (!string.IsNullOrEmpty(ctaUrl) && !string.IsNullOrEmpty(ctaText))
        {
            ctaSection = $"""
                <tr>
                    <td style="padding:8px 40px 24px 40px;">
                        <table role="presentation" cellspacing="0" cellpadding="0" border="0" style="margin:0 auto;">
                            <tr>
                                <td style="border-radius:6px;background-color:#1a73e8;">
                                    <a href="{ctaUrl}" target="_blank" style="display:inline-block;padding:14px 32px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:15px;font-weight:600;color:#ffffff;text-decoration:none;border-radius:6px;">
                                        {ctaText}
                                    </a>
                                </td>
                            </tr>
                        </table>
                    </td>
                </tr>
            """;
        }

        var codeSection = "";
        if (!string.IsNullOrEmpty(highlightCode))
        {
            codeSection = $"""
                <tr>
                    <td style="padding:8px 40px 24px 40px;text-align:center;">
                        <div style="display:inline-block;padding:16px 32px;background-color:#f0f4ff;border:2px dashed #1a73e8;border-radius:8px;font-family:'Courier New',Courier,monospace;font-size:32px;font-weight:700;letter-spacing:8px;color:#1a73e8;">
                            {EscapeHtml(highlightCode)}
                        </div>
                    </td>
                </tr>
            """;
        }

        var additionalSection = "";
        if (!string.IsNullOrEmpty(additionalInfo))
        {
            additionalSection = $"""
                <tr>
                    <td style="padding:0 40px 24px 40px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:13px;line-height:20px;color:#5f6368;">
                        {additionalInfo}
                    </td>
                </tr>
            """;
        }

        var footerSection = "";
        if (!string.IsNullOrEmpty(footer))
        {
            footerSection = $"""
                <tr>
                    <td style="padding:0 40px 24px 40px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:12px;line-height:18px;color:#9aa0a6;border-top:1px solid #e8eaed;padding-top:16px;">
                        {footer}
                    </td>
                </tr>
            """;
        }

        var headerContent = !string.IsNullOrEmpty(headerHtml)
            ? headerHtml
            : !string.IsNullOrEmpty(logoUrl)
                ? $"""<img src="{logoUrl}" alt="Logo" style="height:32px;max-width:200px;" />"""
                : """&#128274; IAM System""";

        var brandFooterSection = !string.IsNullOrEmpty(footerHtml)
            ? footerHtml
            : whiteLabel
                ? ""
                : """
                    Sent by IAM System &mdash; Identity &amp; Access Management<br/>
                    This is an automated message. Please do not reply directly.
                """;

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8"/>
                <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
                <title>IAM System</title>
            </head>
            <body style="margin:0;padding:0;background-color:#f4f5f7;-webkit-font-smoothing:antialiased;">
                <table role="presentation" cellspacing="0" cellpadding="0" border="0" width="100%" style="background-color:#f4f5f7;">
                    <tr>
                        <td style="padding:40px 20px;">
                            <table role="presentation" cellspacing="0" cellpadding="0" border="0" width="560" style="margin:0 auto;max-width:560px;">
                                <!-- Header -->
                                <tr>
                                    <td style="padding:24px 40px;background-color:#1a73e8;border-radius:8px 8px 0 0;">
                                        <table role="presentation" cellspacing="0" cellpadding="0" border="0" width="100%">
                                            <tr>
                                                <td style="font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:20px;font-weight:700;color:#ffffff;letter-spacing:-0.2px;">
                                                    {headerContent}
                                                </td>
                                            </tr>
                                        </table>
                                    </td>
                                </tr>
                                <!-- Body -->
                                <tr>
                                    <td style="background-color:#ffffff;border-radius:0 0 8px 8px;box-shadow:0 1px 3px rgba(0,0,0,0.08);">
                                        <table role="presentation" cellspacing="0" cellpadding="0" border="0" width="100%">
                                            <!-- Greeting -->
                                            <tr>
                                                <td style="padding:32px 40px 8px 40px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:15px;font-weight:600;color:#202124;">
                                                    {greeting}
                                                </td>
                                            </tr>
                                            <!-- Main message -->
                                            <tr>
                                                <td style="padding:8px 40px 24px 40px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:14px;line-height:22px;color:#3c4043;">
                                                    {mainMessage}
                                                </td>
                                            </tr>
                                            {codeSection}
                                            {ctaSection}
                                            {additionalSection}
                                            {footerSection}
                                        </table>
                                    </td>
                                </tr>
                                <!-- Footer branding -->
                                <tr>
                                    <td style="padding:24px 40px;text-align:center;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:11px;color:#9aa0a6;">
                                        {brandFooterSection}
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                </table>
            </body>
            </html>
            """;
    }

    private static string EscapeHtml(string input)
    {
        return System.Net.WebUtility.HtmlEncode(input);
    }

    private static string StripHtmlForLog(string html)
    {
        // Simple HTML stripping for dev-mode console output
        var text = StripTagsRegex().Replace(html, "");
        // Collapse whitespace
        text = CollapseWhitespaceRegex().Replace(text, " ").Trim();
        // Decode common entities
        text = System.Net.WebUtility.HtmlDecode(text);
        return text;
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex StripTagsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$")]
    private static partial Regex EmailFormatRegex();
}
