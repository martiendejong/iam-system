using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Task 5576: outside Development IAM must not run with e-mail sending disabled, because every
/// reset / verification / invitation mail would be silently dropped while callers see success.
/// </summary>
public static class MessagingStartupCheck
{
    public static void Validate(EmailSettings email, SmsSettings sms, ILogger logger)
    {
        if (!email.EnableSending && !email.DisabledFallbackAllowed)
        {
            throw new InvalidOperationException(
                "E-mail sending is disabled (Email:EnableSending is false or the Email section is missing/misspelled) " +
                "outside the Development environment. Configure the Email section with EnableSending=true, or set " +
                "Email:AllowDisabledSending=true to deliberately run a test environment without mail.");
        }

        // SMS is optional in production (the live deployment has no Sms section), so it does not stop
        // startup; SmsService itself fails closed (returns false, logs no body) when sending is disabled.
        if (!sms.EnableSending && !sms.DisabledFallbackAllowed)
        {
            logger.LogWarning(
                "SMS sending is disabled (Sms:EnableSending is false or the Sms section is missing). SMS login codes " +
                "will be reported as not sent. Set Sms:AllowDisabledSending=true to silence this.");
        }
    }
}
