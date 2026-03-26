using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for account linking, multi-identity management, and duplicate account merging.
/// </summary>
public interface IAccountLinkingService
{
    /// <summary>
    /// Link an external provider to an authenticated user by directly specifying the external identity details.
    /// </summary>
    Task<ExternalLogin> LinkExternalProviderAsync(
        Guid userId,
        string provider,
        string providerUserId,
        string? email,
        string? displayName,
        CancellationToken ct = default);

    /// <summary>
    /// Unlink an external provider from a user. Prevents unlinking if it's the last authentication method
    /// (i.e., user has no password and this is the only external login).
    /// </summary>
    Task<AccountLinkingResult> UnlinkExternalProviderAsync(
        Guid userId,
        string provider,
        CancellationToken ct = default);

    /// <summary>
    /// Get all linked external identities for a user.
    /// </summary>
    Task<List<LinkedIdentityDto>> GetLinkedIdentitiesAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Merge a secondary (duplicate) account into the primary account.
    /// Moves all external logins, roles, and audit references from the secondary to the primary.
    /// The secondary account is deactivated after the merge.
    /// </summary>
    Task<AccountLinkingResult> MergeAccountsAsync(
        Guid primaryUserId,
        Guid secondaryUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Set a specific external login as the user's primary identity.
    /// </summary>
    Task<AccountLinkingResult> SetPrimaryIdentityAsync(
        Guid userId,
        Guid externalLoginId,
        CancellationToken ct = default);

    /// <summary>
    /// Detect accounts with the same email that might need merging.
    /// Returns groups of users who share email addresses with external logins.
    /// </summary>
    Task<List<DuplicateEmailGroup>> DetectDuplicateEmailAsync(
        string? email = null,
        CancellationToken ct = default);
}

// ─── DTOs ─────────────────────────────────────────────────

public class AccountLinkingResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    public static AccountLinkingResult Ok() => new() { Success = true };
    public static AccountLinkingResult Fail(string error) => new() { Success = false, Error = error };
}

public class LinkedIdentityDto
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderUserId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public DateTime LinkedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsPrimary { get; set; }
}

public class DuplicateEmailGroup
{
    public string Email { get; set; } = string.Empty;
    public List<DuplicateAccountInfo> Accounts { get; set; } = new();
}

public class DuplicateAccountInfo
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public bool HasPassword { get; set; }
    public List<string> LinkedProviders { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
