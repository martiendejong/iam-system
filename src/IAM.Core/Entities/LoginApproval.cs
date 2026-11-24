namespace IAM.Core.Entities;

public enum LoginApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    Consumed = 3,
    Expired = 4
}

/// <summary>
/// A sign-in that one browser started and another, already signed-in device approves (QR code, push notification or
/// popup). The browser that started it keeps a secret and is the only one that can finish the sign-in; the approver
/// must pick the number the new browser shows (number matching), so approving a request you did not start is hard.
/// </summary>
public class LoginApproval
{
    /// <summary>Public identifier: this is what the QR code and the push notification carry. Random, not sequential.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 (hex) of the secret held by the browser that started the sign-in.</summary>
    public string PollSecretHash { get; set; } = string.Empty;

    /// <summary>The account this request is for: set when the browser typed an e-mail address, otherwise set by whoever approves.</summary>
    public Guid? UserId { get; set; }

    public LoginApprovalStatus Status { get; set; } = LoginApprovalStatus.Pending;

    /// <summary>Two-digit number shown on the new browser; the approver has to choose it from three.</summary>
    public int MatchNumber { get; set; }

    /// <summary>Wrong numbers chosen so far. Three wrong choices deny the request.</summary>
    public int FailedMatches { get; set; }

    public bool RememberMe { get; set; }

    public string? RequestIp { get; set; }
    public string? RequestUserAgent { get; set; }
    public string? RequestDevice { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    /// <summary>The refresh token (sign-in) of the device that approved, for the audit trail.</summary>
    public Guid? ApprovedByRefreshTokenId { get; set; }

    /// <summary>Optimistic concurrency token: every state change bumps it, so two racing writers cannot both win.</summary>
    public int Version { get; set; }
}

/// <summary>A browser/app push endpoint (Web Push) that gets a notification when a sign-in waits for this user.</summary>
public class WebPushSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSuccessAt { get; set; }
    public int FailureCount { get; set; }
}
