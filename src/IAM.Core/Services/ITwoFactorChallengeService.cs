namespace IAM.Core.Services;

/// <summary>
/// Task 5566: short-lived, single-use challenge token that every first sign-in step (password, e-mail/SMS code,
/// magic link, passkey, social) hands out when it ends in the e-mailed 2FA prompt. The resend and verify steps
/// only act for a caller that holds a valid token for that user, so knowing a user id is not enough.
/// </summary>
public interface ITwoFactorChallengeService
{
    /// <summary>Issues a new token bound to this user (and to the login-2FA purpose).</summary>
    string Issue(Guid userId);

    /// <summary>True when the token exists, is unexpired, unused and was issued for exactly this user. Does not consume it.</summary>
    bool IsValid(string? token, Guid userId);

    /// <summary>Burns the token (called after a successful verify). Returns false when it was not valid for this user.</summary>
    bool TryConsume(string? token, Guid userId);
}
