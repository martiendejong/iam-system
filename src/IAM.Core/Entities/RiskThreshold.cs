namespace IAM.Core.Entities;

/// <summary>
/// Configurable risk score thresholds per tenant.
/// Determines when to allow, challenge, or block login attempts.
/// </summary>
public class RiskThreshold
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant this threshold configuration applies to. Null = global default.
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Scores at or below this value are considered low risk (green).
    /// Default: 20
    /// </summary>
    public int LowThreshold { get; set; } = 20;

    /// <summary>
    /// Scores at or below this value are considered medium risk (yellow).
    /// Default: 50
    /// </summary>
    public int MediumThreshold { get; set; } = 50;

    /// <summary>
    /// Scores at or below this value are considered high risk (orange).
    /// Default: 75
    /// </summary>
    public int HighThreshold { get; set; } = 75;

    /// <summary>
    /// Scores above this value result in automatic block (red).
    /// Default: 90
    /// </summary>
    public int BlockThreshold { get; set; } = 90;

    /// <summary>
    /// Require MFA step-up for scores above this value.
    /// Default: 40
    /// </summary>
    public int RequireMfaAbove { get; set; } = 40;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
