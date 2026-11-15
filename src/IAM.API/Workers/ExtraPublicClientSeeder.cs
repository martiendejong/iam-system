using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace IAM.API.Workers;

/// <summary>One configured public PKCE client (Seeder:ExtraPublicClients).</summary>
public sealed class ExtraPublicClientOptions
{
    public string ClientId { get; set; } = "";
    public string? DisplayName { get; set; }
    public List<string> RedirectUris { get; set; } = new();
    public List<string> PostLogoutRedirectUris { get; set; } = new();
}

/// <summary>
/// Creates public PKCE clients from configuration, replacing manual SQL on standalone customer IAMs:
///
///   Seeder__ExtraPublicClients__0__ClientId=taskmanager
///   Seeder__ExtraPublicClients__0__DisplayName=Taskmanager
///   Seeder__ExtraPublicClients__0__RedirectUris__0=https://tasks.example.nl/api/auth/callback
///   Seeder__ExtraPublicClients__0__PostLogoutRedirectUris__0=https://tasks.example.nl/
///
/// or as one JSON array in Seeder__ExtraPublicClientsJson (easier to pass through docker compose).
/// Recipe = the jengo-meeting client: authorization_code + refresh_token, code response, scopes
/// openid profile email roles, PKCE required, implicit consent. A missing client is created; an
/// existing one only has its redirect and post-logout URIs reconciled to the configured lists.
/// </summary>
public static class ExtraPublicClientSeeder
{
    public const string ConfigSection = "Seeder:ExtraPublicClients";
    public const string JsonConfigKey = "Seeder:ExtraPublicClientsJson";

    public static IReadOnlyList<ExtraPublicClientOptions> Resolve(IConfiguration configuration, ILogger logger)
    {
        var all = new List<ExtraPublicClientOptions>();
        all.AddRange(configuration.GetSection(ConfigSection).Get<List<ExtraPublicClientOptions>>() ?? new());

        var json = configuration[JsonConfigKey];
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<ExtraPublicClientOptions>>(
                    json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null) all.AddRange(parsed);
            }
            catch (System.Text.Json.JsonException ex)
            {
                logger.LogWarning("{Key} is not valid JSON and is ignored: {Message}", JsonConfigKey, ex.Message);
            }
        }

        var result = new List<ExtraPublicClientOptions>();
        foreach (var client in all)
        {
            if (string.IsNullOrWhiteSpace(client.ClientId)) continue; // empty compose pass-through slot
            var clientId = client.ClientId.Trim();
            if (result.Any(r => r.ClientId == clientId))
            {
                logger.LogWarning("Seeder extra public client '{ClientId}' is configured twice; the first entry wins.", clientId);
                continue;
            }
            client.ClientId = clientId;
            client.RedirectUris = Clean(client.RedirectUris);
            client.PostLogoutRedirectUris = Clean(client.PostLogoutRedirectUris);
            result.Add(client);
        }
        return result;
    }

    private static List<string> Clean(IEnumerable<string>? uris) =>
        (uris ?? Enumerable.Empty<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static async Task SeedAsync(
        IOpenIddictApplicationManager manager,
        IEnumerable<ExtraPublicClientOptions> clients,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var client in clients)
        {
            var redirect = ParseUris(client.ClientId, client.RedirectUris, logger);
            var postLogout = ParseUris(client.ClientId, client.PostLogoutRedirectUris, logger);

            var existing = await manager.FindByClientIdAsync(client.ClientId, cancellationToken);
            if (existing is null)
            {
                var descriptor = new OpenIddictApplicationDescriptor
                {
                    ClientId = client.ClientId,
                    ClientType = OpenIddictConstants.ClientTypes.Public,
                    ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                    DisplayName = string.IsNullOrWhiteSpace(client.DisplayName) ? client.ClientId : client.DisplayName,
                    Permissions =
                    {
                        OpenIddictConstants.Permissions.Endpoints.Authorization,
                        OpenIddictConstants.Permissions.Endpoints.Token,
                        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                        OpenIddictConstants.Permissions.ResponseTypes.Code,
                        $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.OpenId}",
                        $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Profile}",
                        $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Email}",
                        $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Roles}"
                    },
                    Requirements =
                    {
                        OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange
                    }
                };
                foreach (var u in redirect) descriptor.RedirectUris.Add(u);
                foreach (var u in postLogout) descriptor.PostLogoutRedirectUris.Add(u);
                await manager.CreateAsync(descriptor, cancellationToken);
                logger.LogInformation("Seeded extra public client '{ClientId}'.", client.ClientId);
                continue;
            }

            var current = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(current, existing, cancellationToken);
            var changed = false;
            if (!current.RedirectUris.SetEquals(redirect))
            {
                current.RedirectUris.Clear();
                foreach (var u in redirect) current.RedirectUris.Add(u);
                changed = true;
            }
            if (!current.PostLogoutRedirectUris.SetEquals(postLogout))
            {
                current.PostLogoutRedirectUris.Clear();
                foreach (var u in postLogout) current.PostLogoutRedirectUris.Add(u);
                changed = true;
            }
            if (changed)
            {
                await manager.UpdateAsync(existing, current, cancellationToken);
                logger.LogInformation("Updated redirect URIs of extra public client '{ClientId}'.", client.ClientId);
            }
        }
    }

    private static List<Uri> ParseUris(string clientId, IEnumerable<string> raw, ILogger logger)
    {
        var uris = new List<Uri>();
        foreach (var value in raw)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) uris.Add(uri);
            else logger.LogWarning("Extra public client '{ClientId}': '{Uri}' is not an absolute URI and is skipped.", clientId, value);
        }
        return uris;
    }
}
