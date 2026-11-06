namespace IAM.Core.Services;

public enum PasskeyChallengeKind
{
    Registration,
    Authentication
}

/// <summary>A pending WebAuthn challenge. <c>UserId</c> is null for decoy (unknown / passkey-less) logins.</summary>
public sealed record PasskeyChallenge(byte[] Challenge, Guid? UserId);

/// <summary>
/// Thread-safe, expiring, single-use store for pending passkey challenges, keyed by the random challenge itself
/// (never by e-mail or user id, so concurrent begin calls cannot overwrite each other).
/// </summary>
public interface IPasskeyChallengeStore
{
    void Put(PasskeyChallengeKind kind, byte[] challenge, Guid? userId);

    /// <summary>
    /// Atomically removes and returns the challenge, or null when it is unknown, expired or already used.
    /// Callers must take the challenge BEFORE verifying the signature so a failed attempt also burns it.
    /// </summary>
    PasskeyChallenge? Take(PasskeyChallengeKind kind, string challengeBase64Url);
}
