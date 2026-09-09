namespace IAM.Core;

public static class AuthConstants
{
    /// <summary>
    /// Minimum refresh-token / session cookie lifetime, in days, for a session created
    /// (or rotated) with "Remember me" checked - applied at login and re-applied on
    /// every refresh-token rotation, regardless of the organization's configured
    /// (or default) refresh token lifetime.
    /// </summary>
    public const int RememberMeMinimumDays = 30;
}
