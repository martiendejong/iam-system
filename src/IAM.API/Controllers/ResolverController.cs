using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace IAM.API.Controllers;

/// <summary>
/// Who-decides resolver: returns the ordered authority chain for a principal (task 4059).
///
/// Access is limited to the vault's confidential client, which authenticates via
/// client_credentials and must present a token that includes the "iam_resolver" scope.
/// User tokens and every other client get 403.
///
/// The endpoint is intentionally narrow: it answers "who is in the authority chain
/// for this principal?" without exposing any user profile data beyond what the vault
/// already stores. An unknown principal returns 404; a principal with no manager chain
/// returns 200 with an empty chain.
/// </summary>
[ApiController]
[Route("api/resolver")]
[Authorize(
    AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    Policy = ResolverController.PolicyName)]
public class ResolverController : ControllerBase
{
    /// <summary>Name of the authorization policy that gates every resolver endpoint.</summary>
    public const string PolicyName = "VaultResolverPolicy";

    /// <summary>Scope the vault client must request to call this endpoint.</summary>
    public const string ResolverScope = "iam_resolver";

    private readonly IResolverService _resolver;

    public ResolverController(IResolverService resolver)
    {
        _resolver = resolver;
    }

    /// <summary>
    /// Returns the authority chain for a user principal.
    /// Chain starts with the user's direct manager; the user themselves is not included.
    /// </summary>
    /// <response code="200">Chain resolved (may be empty when the user has no manager).</response>
    /// <response code="404">User not found.</response>
    [HttpGet("users/{userId:guid}/chain")]
    [ResponseCache(Duration = 60, VaryByHeader = "Authorization")]
    public async Task<IActionResult> GetUserChainAsync(Guid userId, CancellationToken ct)
    {
        var result = await _resolver.ResolveUserChainAsync(userId, ct);
        return ToActionResult(userId, "user", result);
    }

    /// <summary>
    /// Returns the authority chain for a service-account principal.
    /// Chain starts with the service account's direct manager (always a User).
    /// </summary>
    [HttpGet("service-accounts/{serviceAccountId:guid}/chain")]
    [ResponseCache(Duration = 60, VaryByHeader = "Authorization")]
    public async Task<IActionResult> GetServiceAccountChainAsync(Guid serviceAccountId, CancellationToken ct)
    {
        var result = await _resolver.ResolveServiceAccountChainAsync(serviceAccountId, ct);
        return ToActionResult(serviceAccountId, "service-account", result);
    }

    /// <summary>
    /// Returns the authority list for a group target: owners first, then admins.
    /// Consumed by access-request flow T11.
    /// </summary>
    [HttpGet("groups/{groupId:guid}/chain")]
    [ResponseCache(Duration = 60, VaryByHeader = "Authorization")]
    public async Task<IActionResult> GetGroupChainAsync(Guid groupId, CancellationToken ct)
    {
        var result = await _resolver.ResolveGroupChainAsync(groupId, ct);
        return ToActionResult(groupId, "group", result);
    }

    private IActionResult ToActionResult(Guid principalId, string kind, ResolverResult result)
    {
        return result.Status switch
        {
            ResolverStatus.NotFound => NotFound(new { error = result.Error ?? $"{kind} not found" }),
            ResolverStatus.InvalidInput => BadRequest(new { error = result.Error }),
            ResolverStatus.Success => Ok(new
            {
                principalId,
                kind,
                chain = result.Chain,
                truncatedAt8Hops = result.TruncatedAt8Hops
            }),
            _ => StatusCode(500, new { error = "Unexpected resolver status" })
        };
    }
}
