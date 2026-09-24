using Hazina.Security.ApiKeys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// Key introspection for the other Jengo apps (Hazina.Security.ApiKeys <c>UseHttpLookup</c>): given the SHA-256 hash of a
/// presented key, returns the key's scope / tenant / expiry so the calling app can validate it locally and cache the
/// answer for a few minutes. Only the hash travels, never a raw key. Inactive and expired keys ARE returned - the
/// calling app's validator rejects them, and the audit trail there can tell "revoked" from "unknown".
/// </summary>
/// <remarks>
/// Deliberately its own controller with only the Hazina policy (no class-level <c>[Authorize]</c>): the caller is always
/// an API key, never a browser session. Restricted to platform-wide admin keys so a tenant-scoped key cannot probe
/// other tenants' keys.
/// </remarks>
[ApiController]
[Route("api/api-keys/introspect")]
public class ApiKeyIntrospectionController : ControllerBase
{
    private readonly IApiKeyLookup _store;

    public ApiKeyIntrospectionController(IApiKeyLookup store) => _store = store;

    [HttpPost]
    [Authorize(Policy = HazinaApiKeyPolicies.Admin)]
    public async Task<IActionResult> Introspect([FromBody] ApiKeyIntrospectionRequest request)
    {
        if (!User.IsPlatformKey())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Key introspection requires a platform-wide admin API key." });

        var hash = request.KeyHash?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            return BadRequest(new { error = "keyHash must be the 64-character hex SHA-256 of the key." });

        var record = await _store.FindByHashAsync(hash, HttpContext.RequestAborted);
        return record is null ? NotFound() : Ok(ApiKeyIntrospection.FromRecord(record));
    }
}
