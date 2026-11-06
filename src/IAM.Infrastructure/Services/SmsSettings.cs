namespace IAM.Infrastructure.Services;

public class SmsSettings
{
    public string Provider { get; set; } = "Twilio";
    public string AccountSid { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public string FromNumber { get; set; } = string.Empty;
    public bool EnableSending { get; set; } = false; // Default off for dev

    /// <summary>Deliberate opt-out for test environments outside Development. Default off (fail closed).</summary>
    public bool AllowDisabledSending { get; set; } = false;

    /// <summary>Set by the host (true only in the Development environment); not bound from configuration.</summary>
    public bool IsDevelopmentEnvironment { get; set; } = false;

    public bool DisabledFallbackAllowed => IsDevelopmentEnvironment || AllowDisabledSending;
}
