namespace IAM.Core.Entities;

/// <summary>
/// Task 5852: one row per (typed e-mail, source address) that has failed a password sign-in, holding
/// the time of the latest failure. Counting rows still inside the window gives the number of
/// DISTINCT sources guessing at one account, and the table stays bounded (a pair never adds more
/// than one row). Both keys are keyed hashes, so the table neither reveals which e-mails belong to
/// real accounts nor stores addresses or passwords. Persisted because IMemoryCache is lost on every
/// restart or app-pool recycle.
/// </summary>
public class LoginFailureSignal
{
    /// <summary>HMAC of the normalized typed e-mail (the same whether or not an account exists).</summary>
    public string AccountKey { get; set; } = string.Empty;

    /// <summary>HMAC of the normalized source address (IPv6 keyed on its /64).</summary>
    public string SourceKey { get; set; } = string.Empty;

    public DateTime LastFailureAt { get; set; }
}

/// <summary>
/// Task 5852: marks that the security alert for an account has been raised, so it fires once per
/// account per window and a flood of failures cannot flood the alert channels.
/// </summary>
public class AccountGuessingAlertMark
{
    public string AccountKey { get; set; } = string.Empty;
    public DateTime AlertedAt { get; set; }
}
