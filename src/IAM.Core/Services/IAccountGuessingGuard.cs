namespace IAM.Core.Services;

/// <summary>
/// Task 5852: a per-ACCOUNT brake on distributed password guessing. The per-IP and per-(e-mail, IP)
/// throttle (task 5166) cannot see an attacker who spreads guesses at one e-mail over many
/// addresses. This counts failures per typed e-mail across all sources; once enough distinct
/// sources have failed inside the window the account needs an e-mailed code on top of a correct
/// password (the real owner is never locked out) and one security alert is raised.
/// </summary>
public interface IAccountGuessingGuard
{
    /// <summary>
    /// Records one failed password attempt (call it identically for unknown and known e-mails).
    /// <paramref name="accountExists"/> only decides whether an alert is raised; nothing the caller
    /// can observe depends on it.
    /// </summary>
    Task RecordFailureAsync(string? email, string? ipAddress, Guid? accountId, bool accountExists, CancellationToken ct = default);

    /// <summary>True when a correct password alone must not be enough for this e-mail right now.</summary>
    Task<bool> IsStepUpRequiredAsync(string? email, CancellationToken ct = default);
}
