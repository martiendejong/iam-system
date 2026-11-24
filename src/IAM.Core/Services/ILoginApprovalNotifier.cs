using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>Tells a user's registered devices that a sign-in waits for approval (Web Push). Best effort: never throws.</summary>
public interface ILoginApprovalNotifier
{
    Task NotifyAsync(Guid userId, LoginApproval approval, CancellationToken ct = default);
}
