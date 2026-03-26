namespace IAM.Core.Entities;

/// <summary>
/// Records the risk assessment for each login attempt.
/// Used for adaptive authentication decisions (allow, step-up MFA, block).
/// </summary>
public class LoginRiskScore
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// IP address of the login attempt
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// Browser/client user agent string
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Geo-location derived from IP (e.g. "Amsterdam, NL")
    /// </summary>
    public string? GeoLocation { get; set; }

    /// <summary>
    /// Computed risk score from 0 (safe) to 100 (critical)
    /// </summary>
    public int RiskScore { get; set; }

    /// <summary>
    /// JSON array of individual risk factors that contributed to the score.
    /// Example: [{"factor":"new_device","points":20,"description":"First login from this device"}]
    /// </summary>
    public string RiskFactors { get; set; } = "[]";

    /// <summary>
    /// Action taken based on the risk score: Allow, StepUp, Block
    /// </summary>
    public RiskAction Action { get; set; } = RiskAction.Allow;

    /// <summary>
    /// Associated session ID (if login was allowed)
    /// </summary>
    public Guid? SessionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Action taken based on login risk assessment
/// </summary>
public enum RiskAction
{
    /// <summary>Login allowed without additional verification</summary>
    Allow = 0,

    /// <summary>Step-up authentication required (MFA challenge)</summary>
    StepUp = 1,

    /// <summary>Login blocked due to high risk</summary>
    Block = 2
}
