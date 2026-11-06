using System.Text.RegularExpressions;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace IAM.API.Workers;

/// <summary>One WordPress site that signs in through the Jengo IAM SSO plugin (task 5804).</summary>
public sealed record WordPressSsoSite(string ClientId, string DisplayName, string SiteUrl, string? Secret)
{
    /// <summary>The plugin's callback: wp-login.php with action=jengo-iam-callback. Must match byte-for-byte.</summary>
    public string RedirectUri => $"{SiteUrl.TrimEnd('/')}/wp-login.php?action=jengo-iam-callback";

    /// <summary>The home page, where the plugin sends the browser after the IAM session ended.</summary>
    public string PostLogoutRedirectUri => $"{SiteUrl.TrimEnd('/')}/";

    public string RoleCategory => $"app:{ClientId}";
}

/// <summary>
/// Registers the OAuth client and the app-role catalog for every WordPress site that uses the
/// Jengo IAM SSO plugin. The admin API for creating clients is not implemented (it returns 501), so
/// clients are registered at startup, like every other client in <see cref="DatabaseSeeder"/>, but
/// driven by configuration so adding a site needs no code change:
///
///   "WordPressSso": { "Sites": [ {
///       "ClientId": "wordpress-example-com",
///       "DisplayName": "WordPress (example.com)",
///       "SiteUrl": "https://example.com/",
///       "Secret": "(set on the IAM host: environment or an untracked appsettings override)"
///   } ] }
///
/// test.prospergenics.com is built in; its secret comes from Clients:WordPressTestProspergenics:Secret.
/// A site without a secret is not registered (a warning is logged), so no client ever exists with a
/// known or empty secret. Both steps are idempotent and never modify or delete anything that exists.
/// </summary>
public static class WordPressSsoSeeder
{
    public const string TestSiteClientId = "wordpress-test-prospergenics";
    public const string TestSiteSecretConfigKey = "Clients:WordPressTestProspergenics:Secret";
    public const string SitesConfigSection = "WordPressSso:Sites";

    /// <summary>The WordPress roles the app-role catalog offers, in the order the plugin ranks them.</summary>
    public static readonly (string Role, string Description)[] RoleCatalog =
    {
        ("administrator", "WordPress administrator (full site admin)"),
        ("editor", "WordPress editor (manage and publish all content)"),
        ("author", "WordPress author (publish own content)"),
        ("subscriber", "WordPress subscriber (read-only account)")
    };

    private static readonly Regex ClientIdPattern = new("^[a-z0-9][a-z0-9-]{2,63}$", RegexOptions.Compiled);

    /// <summary>The built-in test site plus every valid entry under WordPressSso:Sites.</summary>
    public static IReadOnlyList<WordPressSsoSite> ResolveSites(IConfiguration configuration, ILogger logger)
    {
        var sites = new List<WordPressSsoSite>
        {
            new(TestSiteClientId, "WordPress (test.prospergenics.com)", "https://test.prospergenics.com/",
                configuration[TestSiteSecretConfigKey])
        };

        foreach (var entry in configuration.GetSection(SitesConfigSection).GetChildren())
        {
            var clientId = (entry["ClientId"] ?? string.Empty).Trim().ToLowerInvariant();
            var siteUrl = (entry["SiteUrl"] ?? string.Empty).Trim();

            if (!ClientIdPattern.IsMatch(clientId))
            {
                logger.LogWarning("WordPressSso site skipped: ClientId '{ClientId}' must be 3-64 characters of a-z, 0-9 and '-'.", clientId);
                continue;
            }

            if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                logger.LogWarning("WordPressSso site '{ClientId}' skipped: SiteUrl must be an absolute https URL without query or fragment.", clientId);
                continue;
            }

            if (sites.Any(s => string.Equals(s.ClientId, clientId, StringComparison.Ordinal)))
            {
                logger.LogWarning("WordPressSso site '{ClientId}' skipped: that ClientId is already configured.", clientId);
                continue;
            }

            var displayName = entry["DisplayName"];
            sites.Add(new WordPressSsoSite(
                clientId,
                string.IsNullOrWhiteSpace(displayName) ? $"WordPress ({uri.Host})" : displayName.Trim(),
                uri.GetLeftPart(UriPartial.Path),
                entry["Secret"]));
        }

        return sites;
    }

    /// <summary>
    /// Registers the confidential OAuth client of every site that has a secret: authorization code flow
    /// with PKCE required, no refresh token grant (the plugin never asks for offline access), the end
    /// session endpoint so "also log out of IAM" works, and exactly the site's redirect URIs.
    /// </summary>
    public static async Task SeedClientsAsync(
        IOpenIddictApplicationManager manager,
        IReadOnlyList<WordPressSsoSite> sites,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var site in sites)
        {
            if (string.IsNullOrEmpty(site.Secret))
            {
                logger.LogWarning(
                    "No client secret configured for WordPress SSO site '{ClientId}': the OAuth client will NOT be registered. " +
                    "Set {Key} to enable it (task 5804).",
                    site.ClientId,
                    site.ClientId == TestSiteClientId ? TestSiteSecretConfigKey : $"{SitesConfigSection}:<n>:Secret");
                continue;
            }

            if (await manager.FindByClientIdAsync(site.ClientId, cancellationToken) != null)
                continue;

            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = site.ClientId,
                ClientSecret = site.Secret,
                ClientType = OpenIddictConstants.ClientTypes.Confidential,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = site.DisplayName,
                RedirectUris = { new Uri(site.RedirectUri) },
                PostLogoutRedirectUris = { new Uri(site.PostLogoutRedirectUri) },
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.Endpoints.EndSession,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
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
            }, cancellationToken);

            logger.LogInformation("WordPress SSO OAuth client '{ClientId}' registered for {SiteUrl}", site.ClientId, site.SiteUrl);
        }
    }

    /// <summary>
    /// Seeds the federated app-role catalog of every site, same shape as AppRolesController.Register
    /// produces: global roles named "{clientId}:{role}" with Category "app:{clientId}", so the access
    /// matrix and the OIDC app-role gate (AuthorizationController) pick them up without more wiring.
    /// Seeding a catalog activates the gate for that client: only users holding at least one of its roles
    /// can sign in. Creates missing roles only.
    /// </summary>
    public static async Task SeedAppRolesAsync(
        IAMDbContext context,
        IReadOnlyList<WordPressSsoSite> sites,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var site in sites)
        {
            try
            {
                var existing = await context.Roles
                    .Where(r => r.Category == site.RoleCategory)
                    .Select(r => r.Name)
                    .ToListAsync(cancellationToken);

                var added = 0;
                foreach (var (role, description) in RoleCatalog)
                {
                    var fullName = $"{site.ClientId}:{role}";
                    if (existing.Contains(fullName, StringComparer.OrdinalIgnoreCase))
                        continue;

                    context.Roles.Add(new Role
                    {
                        Name = fullName,
                        TenantId = null, // global, same as AppRolesController.Register
                        Category = site.RoleCategory,
                        Description = description,
                        IsSystemRole = false
                    });
                    added++;
                }

                if (added > 0)
                {
                    await context.SaveChangesAsync(cancellationToken);
                    logger.LogInformation(
                        "WordPress SSO app-role catalog seeded for {ClientId}: {Added} role(s) added", site.ClientId, added);
                }
            }
            catch (Exception ex)
            {
                // Role seeding must never take IAM down; a catalog can also be registered later
                // via POST /api/app-roles/register.
                logger.LogError(ex, "WordPress SSO app-role seeding failed for {ClientId}", site.ClientId);
            }
        }
    }
}
