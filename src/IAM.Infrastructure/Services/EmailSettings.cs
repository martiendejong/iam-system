namespace IAM.Infrastructure.Services;

public class EmailSettings
{
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public bool UseSsl { get; set; } = true;
    public string FromEmail { get; set; } = "noreply@iam-system.local";
    public string FromName { get; set; } = "IAM System";
    public string? BaseUrl { get; set; } = "https://localhost:5161";
    public bool EnableSending { get; set; } = false; // Default off for dev

    /// <summary>
    /// Deliberate opt-out for test environments outside Development: allow running with sending disabled
    /// (messages are then dropped, never delivered). Default off, so a missing/misspelled Email section
    /// fails closed instead of silently pretending to send.
    /// </summary>
    public bool AllowDisabledSending { get; set; } = false;

    /// <summary>Set by the host (true only in the Development environment); not bound from configuration.</summary>
    public bool IsDevelopmentEnvironment { get; set; } = false;

    public bool DisabledFallbackAllowed => IsDevelopmentEnvironment || AllowDisabledSending;
}
