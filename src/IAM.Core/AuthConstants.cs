namespace IAM.Core;

public static class AuthConstants
{
    /// <summary>
    /// Minimum refresh-token / session cookie lifetime, in days, for a session created
    /// (or rotated) with "Remember me" checked - applied at login and re-applied on
    /// every refresh-token rotation, regardless of the organization's configured
    /// (or default) refresh token lifetime.
    /// </summary>
    public const int RememberMeMinimumDays = 90;

    /// <summary>
    /// Seconds during which a refresh token that was JUST rotated may be presented again without being
    /// treated as theft. Two tabs refreshing at once, a retried request or a lost response all replay the
    /// spent token within seconds; only a replay after this window is a theft signal.
    /// </summary>
    public const int RefreshReplayGraceSeconds = 60;
}
