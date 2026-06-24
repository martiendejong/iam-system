using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;

namespace IAM.API.Controllers;

/// <summary>
/// Read-only listing of registered OAuth2/OIDC clients (OpenIddict applications),
/// used by the admin UI (e.g. the Claims Mapping client selector).
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

    [HttpGet("clients")]
    public async Task<IActionResult> GetClients(CancellationToken cancellationToken)
    {
        var clients = new List<object>();

        await foreach (var app in _applicationManager.ListAsync(cancellationToken: cancellationToken))
        {
            var id = await _applicationManager.GetIdAsync(app, cancellationToken);
            var clientId = await _applicationManager.GetClientIdAsync(app, cancellationToken);
            var displayName = await _applicationManager.GetDisplayNameAsync(app, cancellationToken);

            clients.Add(new
            {
                id,
                clientId,
                displayName = string.IsNullOrWhiteSpace(displayName) ? clientId : displayName,
            });
        }

        return Ok(clients);
    }
}
