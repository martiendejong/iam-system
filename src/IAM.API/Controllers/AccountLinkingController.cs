using System.Security.Claims;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// Account linking and multi-identity management for the user self-service portal.
/// Allows users to link/unlink external providers, manage primary identity, and merge duplicate accounts.
/// </summary>
[ApiController]
[Route("api/portal")]
[Authorize]
public class AccountLinkingController : ControllerBase
{
    private readonly IAccountLinkingService _accountLinkingService;
    private readonly ILogger<AccountLinkingController> _logger;

    public AccountLinkingController(
        IAccountLinkingService accountLinkingService,
        ILogger<AccountLinkingController> logger)
    {
        _accountLinkingService = accountLinkingService;
        _logger = logger;
    }

    // ─── Link / Unlink ────────────────────────────────────────

    /// <summary>
    /// Link a new external provider to the current user's account.
    /// POST /api/portal/link/{provider}
    /// </summary>
    [HttpPost("link/{provider}")]
    public async Task<IActionResult> LinkProvider(
        string provider,
        [FromBody] LinkProviderRequest request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        try
        {
            var externalLogin = await _accountLinkingService.LinkExternalProviderAsync(
                userId.Value,
                provider,
                request.ProviderUserId,
                request.Email,
                request.DisplayName,
                ct);

            return Ok(new
            {
                id = externalLogin.Id,
                provider = externalLogin.Provider,
                providerUserId = externalLogin.ProviderUserId,
                email = externalLogin.Email,
                displayName = externalLogin.DisplayName,
                linkedAt = externalLogin.LinkedAt,
                isPrimary = externalLogin.IsPrimary
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Unlink an external provider from the current user's account.
    /// DELETE /api/portal/link/{provider}
    /// </summary>
    [HttpDelete("link/{provider}")]
    public async Task<IActionResult> UnlinkProvider(string provider, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var result = await _accountLinkingService.UnlinkExternalProviderAsync(userId.Value, provider, ct);

        if (!result.Success)
            return BadRequest(new { error = result.Error });

        return Ok(new { message = $"External provider '{provider}' unlinked successfully" });
    }

    // ─── List Identities ──────────────────────────────────────

    /// <summary>
    /// Get all linked external identities for the current user.
    /// GET /api/portal/identities
    /// </summary>
    [HttpGet("identities")]
    public async Task<IActionResult> GetIdentities(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var identities = await _accountLinkingService.GetLinkedIdentitiesAsync(userId.Value, ct);

        return Ok(identities);
    }

    // ─── Set Primary Identity ─────────────────────────────────

    /// <summary>
    /// Set a specific external login as the user's primary identity.
    /// POST /api/portal/identities/{id}/primary
    /// </summary>
    [HttpPost("identities/{id:guid}/primary")]
    public async Task<IActionResult> SetPrimaryIdentity(Guid id, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var result = await _accountLinkingService.SetPrimaryIdentityAsync(userId.Value, id, ct);

        if (!result.Success)
            return BadRequest(new { error = result.Error });

        return Ok(new { message = "Primary identity updated successfully" });
    }

    // ─── Merge Accounts ───────────────────────────────────────

    /// <summary>
    /// Merge a secondary (duplicate) account into the current user's account.
    /// POST /api/portal/merge
    /// </summary>
    [HttpPost("merge")]
    public async Task<IActionResult> MergeAccounts(
        [FromBody] MergeAccountsRequest request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var result = await _accountLinkingService.MergeAccountsAsync(
            userId.Value,
            request.SecondaryUserId,
            ct);

        if (!result.Success)
            return BadRequest(new { error = result.Error });

        return Ok(new { message = "Accounts merged successfully" });
    }

    /// <summary>
    /// Detect potential duplicate accounts that share emails with the current user.
    /// GET /api/portal/merge/suggestions
    /// </summary>
    [HttpGet("merge/suggestions")]
    public async Task<IActionResult> GetMergeSuggestions(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        // Get current user's email to check for duplicates
        var identities = await _accountLinkingService.GetLinkedIdentitiesAsync(userId.Value, ct);

        var emailsToCheck = identities
            .Where(i => !string.IsNullOrEmpty(i.Email))
            .Select(i => i.Email!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allSuggestions = new List<DuplicateEmailGroup>();

        foreach (var email in emailsToCheck)
        {
            var duplicates = await _accountLinkingService.DetectDuplicateEmailAsync(email, ct);

            // Filter out groups that don't involve the current user
            var relevantGroups = duplicates
                .Where(g => g.Accounts.Any(a => a.UserId == userId.Value))
                .ToList();

            allSuggestions.AddRange(relevantGroups);
        }

        // Deduplicate groups by email
        var distinctGroups = allSuggestions
            .GroupBy(g => g.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return Ok(distinctGroups);
    }

    // ─── Helpers ──────────────────────────────────────────────

    private Guid? GetUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(claim, out var userId))
            return userId;

        return null;
    }
}

// ─── Request DTOs ─────────────────────────────────────────

public record LinkProviderRequest(
    string ProviderUserId,
    string? Email,
    string? DisplayName
);

public record MergeAccountsRequest(
    Guid SecondaryUserId
);
