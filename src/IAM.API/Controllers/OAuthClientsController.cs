using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;

namespace IAM.API.Controllers;

/// <summary>
/// Read access to registered OAuth2/OIDC clients (OpenIddict applications) for the admin UI.
///
/// NOTE: create/update/delete are intentionally NOT implemented here. Mutating OpenIddict
/// applications changes live SSO for every relying app (jengo-workspace, open-webui, etc.);
/// the write surface needs an explicit design pass (clientId/secret generation, permission
/// mapping, confidential vs public) before being exposed. The write endpoints return 501.
/// </summary>
[ApiController]
[Route("api/oauth")]
[Authorize(Roles = "SuperAdmin,SystemAdmin")]
public class OAuthClientsController : ControllerBase
{
    private readonly IOpenIddictApplicationManager _applicationManager;

    public OAuthClientsController(IOpenIddictApplicationManager applicationManager)
    {
        _applicationManager = applicationManager;
    }

    // Some applications (e.g. manually SQL-inserted clients) can have a malformed
    // Permissions/RedirectUris column that OpenIddict fails to JSON-parse. Read each
    // collection defensively so one corrupt record doesn't 500 the whole list.
    private async Task<string[]> SafeArray(Func<CancellationToken, ValueTask<System.Collections.Immutable.ImmutableArray<string>>> get, CancellationToken ct)
    {
        try { return (await get(ct)).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    private async Task<object> Project(object app, CancellationToken ct)
    {
        var clientId = await _applicationManager.GetClientIdAsync(app, ct);
        var permissions = await SafeArray(c => _applicationManager.GetPermissionsAsync(app, c), ct);
        string? type = null;
        try { type = await _applicationManager.GetClientTypeAsync(app, ct); } catch { }

        return new
        {
            id = await _applicationManager.GetIdAsync(app, ct),
            clientId,
            displayName = await _applicationManager.GetDisplayNameAsync(app, ct) ?? clientId,
            type,
            redirectUris = await SafeArray(c => _applicationManager.GetRedirectUrisAsync(app, c), ct),
            postLogoutRedirectUris = await SafeArray(c => _applicationManager.GetPostLogoutRedirectUrisAsync(app, c), ct),
            permissions,
            grantTypes = permissions
                .Where(p => p.StartsWith("gt:", StringComparison.Ordinal))
                .Select(p => p.Substring(3))
                .ToArray(),
        };
    }

    [HttpGet("clients")]
    public async Task<IActionResult> GetClients(CancellationToken cancellationToken)
    {
        var clients = new List<object>();
        await foreach (var app in _applicationManager.ListAsync(cancellationToken: cancellationToken))
        {
            clients.Add(await Project(app, cancellationToken));
        }
        return Ok(clients);
    }

    [HttpGet("clients/{id}")]
    public async Task<IActionResult> GetClient(string id, CancellationToken cancellationToken)
    {
        var app = await _applicationManager.FindByIdAsync(id, cancellationToken);
        if (app == null)
            return NotFound(new { error = "Client not found" });

        return Ok(await Project(app, cancellationToken));
    }

    [HttpPost("clients")]
    public IActionResult CreateClient()
        => StatusCode(501, new { error = "Creating OAuth clients via the UI is not enabled yet." });

    [HttpPut("clients/{id}")]
    public IActionResult UpdateClient(string id)
        => StatusCode(501, new { error = "Editing OAuth clients via the UI is not enabled yet." });

    [HttpDelete("clients/{id}")]
    public IActionResult DeleteClient(string id)
        => StatusCode(501, new { error = "Deleting OAuth clients via the UI is not enabled yet." });
}
