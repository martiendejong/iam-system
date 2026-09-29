using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Sets/clears the manager link and principal kind on users and service accounts
/// (task 4057). Shared by UsersController and ServiceAccountsController so both
/// endpoints enforce identical cycle / depth / tenant rules.
/// </summary>
public interface IPrincipalDirectoryService
{
    /// <summary>
    /// Replace both fields on a user: managerUserId (null = clear) and kind.
    /// Writes an audit log entry when anything actually changes.
    /// </summary>
    Task<PrincipalAssignmentResult> SetUserPrincipalAsync(
        Guid userId,
        Guid? managerUserId,
        PrincipalKind kind,
        Guid? actorUserId,
        bool callerIsSuperAdmin,
        CancellationToken ct = default);

    /// <summary>
    /// Replace both fields on a service account: managerUserId (null = clear) and kind.
    /// Writes an audit log entry when anything actually changes.
    /// </summary>
    Task<PrincipalAssignmentResult> SetServiceAccountPrincipalAsync(
        Guid serviceAccountId,
        Guid? managerUserId,
        PrincipalKind kind,
        Guid? actorUserId,
        bool callerIsSuperAdmin,
        CancellationToken ct = default);
}

public enum PrincipalAssignmentStatus
{
    /// <summary>Fields applied (or already had the requested values).</summary>
    Success,

    /// <summary>The target user / service account does not exist (404).</summary>
    TargetNotFound,

    /// <summary>Self-manager, cycle, depth &gt; 8, unknown manager, or cross-tenant manager (400).</summary>
    ValidationFailed
}

public sealed record PrincipalAssignmentResult(
    PrincipalAssignmentStatus Status,
    string? Error = null)
{
    public static PrincipalAssignmentResult Success() => new(PrincipalAssignmentStatus.Success);
    public static PrincipalAssignmentResult NotFound() => new(PrincipalAssignmentStatus.TargetNotFound);
    public static PrincipalAssignmentResult Invalid(string error) => new(PrincipalAssignmentStatus.ValidationFailed, error);
}
