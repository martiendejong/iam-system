namespace IAM.Core.Configuration;

/// <summary>
/// Abuse limits for the self-service onboarding step (task 5496). Bound from the "Onboarding"
/// configuration section.
/// </summary>
public class OnboardingOptions
{
    public const string SectionName = "Onboarding";

    /// <summary>Maximum length of an organization name.</summary>
    public int MaxOrganizationNameLength { get; set; } = 100;

    /// <summary>Invitations one user may send per window (counted over all Invitation rows they created).</summary>
    public int MaxInvitesPerUserPerWindow { get; set; } = 5;

    /// <summary>Window for the per-user limit, in minutes.</summary>
    public int UserWindowMinutes { get; set; } = 60;

    /// <summary>Invitations one recipient address may receive per window, from any sender.</summary>
    public int MaxInvitesPerRecipientPerWindow { get; set; } = 1;

    /// <summary>Window for the per-recipient limit, in minutes.</summary>
    public int RecipientWindowMinutes { get; set; } = 24 * 60;
}
