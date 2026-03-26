namespace IAM.Infrastructure.Services;

public class SmsSettings
{
    public string Provider { get; set; } = "Twilio";
    public string AccountSid { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public string FromNumber { get; set; } = string.Empty;
    public bool EnableSending { get; set; } = false; // Default off for dev
}
