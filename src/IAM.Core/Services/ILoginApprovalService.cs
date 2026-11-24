using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>What the browser that starts a sign-in gets back: its public id, its private secret and the number to show.</summary>
public record LoginApprovalStart(Guid Id, string PollSecret, int MatchNumber, DateTime ExpiresAt);

/// <summary>A pending request as the approving device sees it. The correct number is one of <see cref="Choices"/>, not marked.</summary>
public record LoginApprovalInfo(
    Guid Id,
    string Device,
    string? Ip,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    int[] Choices,
    bool NamedYou);

public enum ApproveOutcome
{
    Approved,
    NotFound,
    NotPending,
    WrongUser,
    WrongNumber,
    TooManyWrong
}

public record ConsumeResult(bool Success, User? User, bool RememberMe, string? Error, LoginApprovalStatus Status);

public interface ILoginApprovalService
{
    /// <summary>
    /// Start a request. With an e-mail address that belongs to an active user the request is tied to that user and a
    /// push goes to their devices. Without an address, or with one that matches no active user, it becomes a QR-only
    /// request (no push, no popup; whoever scans it while signed in signs the browser in as themselves), and the answer
    /// looks identical, so it does not reveal whether the address exists. Returns null when this IP asks too often.
    /// </summary>
    Task<LoginApprovalStart?> StartAsync(string? email, bool rememberMe, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>Status of a request, for the browser that started it. Null when the id/secret pair is wrong.</summary>
    Task<LoginApprovalStatus?> GetStatusAsync(Guid id, string pollSecret, CancellationToken ct = default);

    /// <summary>Finish the sign-in: only an approved, unexpired request with the right secret, and only once.</summary>
    Task<ConsumeResult> ConsumeAsync(Guid id, string pollSecret, CancellationToken ct = default);

    /// <summary>Requests that name this user and still wait, newest first (the popup on an already signed-in device).</summary>
    Task<IReadOnlyList<LoginApprovalInfo>> ListPendingAsync(Guid userId, CancellationToken ct = default);

    /// <summary>One pending request for an approver; null when it does not exist, is decided/expired, or names someone else.</summary>
    Task<LoginApprovalInfo?> GetForApproverAsync(Guid id, Guid approverUserId, CancellationToken ct = default);

    Task<ApproveOutcome> ApproveAsync(Guid id, Guid approverUserId, Guid? approverRefreshTokenId, int chosenNumber, CancellationToken ct = default);

    Task<bool> DenyAsync(Guid id, Guid approverUserId, CancellationToken ct = default);
}
