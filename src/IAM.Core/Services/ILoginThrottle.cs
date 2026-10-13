namespace IAM.Core.Services;

/// <summary>
/// Task 5166: slows down repeated failed password sign-ins instead of locking the real account.
///
/// The throttle is keyed on the TYPED email (not on whether an account exists) plus the caller's
/// IP, so an unknown email is throttled exactly like a real one, and one caller's failures can
/// never block the owner signing in from another address.
/// </summary>
public interface ILoginThrottle
{
    /// <summary>
    /// Asks permission for one password attempt and, when it is allowed, counts it as a failure
    /// straight away (so parallel requests cannot all slip through before the first one is
    /// recorded). Call <see cref="RecordSuccess"/> when the password turns out to be correct.
    /// A refused attempt is not counted, so a blocked caller does not extend its own block.
    /// </summary>
    LoginThrottleDecision BeginAttempt(string? email, string? ipAddress);

    /// <summary>
    /// The password was correct: forget the failures for this (email, IP) pair and give back the
    /// per-IP slot the attempt used.
    /// </summary>
    void RecordSuccess(string? email, string? ipAddress);
}

/// <param name="Allowed">False when the caller has to wait before trying again.</param>
/// <param name="RetryAfterSeconds">Whole seconds to wait (at least 1) when not allowed, else 0.</param>
public readonly record struct LoginThrottleDecision(bool Allowed, int RetryAfterSeconds)
{
    public static LoginThrottleDecision Allow { get; } = new(true, 0);
    public static LoginThrottleDecision Wait(int seconds) => new(false, Math.Max(1, seconds));
}
