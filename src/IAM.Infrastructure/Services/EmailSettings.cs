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
}
