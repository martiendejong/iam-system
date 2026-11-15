using System.Net;
using System.Security.Cryptography;
using IAM.API.Tests.Infrastructure;
using IAM.API.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Tests.Security;

/// <summary>
/// Configurable deployment shape (standalone customer IAM at a host root): Cors:AllowedOrigins, Ui:LoginPath,
/// Seeder:SkipInternalClients and Seeder:ExtraPublicClients. Every default must keep the /auth deployment as it was.
/// </summary>
public class RootHostDeploymentTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    // ----- CORS selection -----------------------------------------------------------------------------

    [Fact]
    public void Cors_NothingConfigured_UsesTheHardcodedDefaultsUnchanged()
    {
        Assert.Equal(
            new[] { "http://localhost:5173", "https://localhost:5173", "https://maendeleo.martiendejong.nl", "https://knowledge.prospergenics.com" },
            DeploymentSettings.ResolveCorsOrigins(Config()));
    }

    [Fact]
    public void Cors_OnlyBlankEntries_FallBackToTheDefaults()
    {
        // docker compose passes Cors__AllowedOrigins__0= (empty) when the variable is unset
        Assert.Equal(DeploymentSettings.DefaultCorsOrigins,
            DeploymentSettings.ResolveCorsOrigins(Config(("Cors:AllowedOrigins:0", ""), ("Cors:AllowedOrigins:1", "  "))));
    }

    [Fact]
    public void Cors_Configured_ReplacesTheListAndNeverAppendsTheDefaults()
    {
        var origins = DeploymentSettings.ResolveCorsOrigins(Config(
            ("Cors:AllowedOrigins:0", "https://tasks.example.nl"),
            ("Cors:AllowedOrigins:1", "https://meet.example.nl/"),
            ("Cors:AllowedOrigins:2", "")));
        Assert.Equal(new[] { "https://tasks.example.nl", "https://meet.example.nl" }, origins);
    }

    private sealed class ConfiguredFactory : IAMTestWebApplicationFactory
    {
        private readonly Dictionary<string, string?> _settings;
        public ConfiguredFactory(Dictionary<string, string?> settings) => _settings = settings;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        }
    }

    private static async Task<string?> PreflightAllowOrigin(IAMTestWebApplicationFactory factory, string origin)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        var response = await client.SendAsync(request);
        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.Single() : null;
    }

    [Fact]
    public async Task Cors_EndToEnd_DefaultKeepsOurOrigins_AndConfiguredListReplacesThem()
    {
        using (var defaults = new IAMTestWebApplicationFactory())
        {
            Assert.Equal("https://maendeleo.martiendejong.nl", await PreflightAllowOrigin(defaults, "https://maendeleo.martiendejong.nl"));
            Assert.Null(await PreflightAllowOrigin(defaults, "https://tasks.example.nl"));
        }

        using var customer = new ConfiguredFactory(new() { ["Cors:AllowedOrigins:0"] = "https://tasks.example.nl" });
        Assert.Equal("https://tasks.example.nl", await PreflightAllowOrigin(customer, "https://tasks.example.nl"));
        Assert.Null(await PreflightAllowOrigin(customer, "https://maendeleo.martiendejong.nl"));
    }

    // ----- login path ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "/auth/login")]
    [InlineData("", "/auth/login")]
    [InlineData("/login", "/login")]
    [InlineData("/login/", "/login")]
    [InlineData("/auth/login", "/auth/login")]
    [InlineData("https://evil.example/login", "/auth/login")]
    [InlineData("//evil.example/login", "/auth/login")]
    [InlineData("login", "/auth/login")]
    [InlineData("/login?x=1", "/auth/login")]
    public void LoginPath_DefaultsToAuthLogin_AndOnlyAcceptsLocalPaths(string? configured, string expected)
    {
        Assert.Equal(expected, DeploymentSettings.ResolveLoginPath(Config(("Ui:LoginPath", configured))));
    }

    [Fact]
    public async Task LoginPath_EndToEnd_AuthorizeRedirectsToTheConfiguredLoginPage()
    {
        using var factory = new ConfiguredFactory(new() { ["Ui:LoginPath"] = "/login" });
        const string redirectUri = "https://tasks.example.nl/api/auth/callback";
        using (var scope = factory.Services.CreateScope())
        {
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            await ExtraPublicClientSeeder.SeedAsync(apps, new[]
            {
                new ExtraPublicClientOptions { ClientId = "taskmanager", RedirectUris = { redirectUri } }
            }, NullLogger.Instance, CancellationToken.None);
        }

        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        var response = await client.GetAsync("/connect/authorize?client_id=taskmanager&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=openid&state=s1" +
            $"&code_challenge={challenge}&code_challenge_method=S256");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login?returnUrl=", response.Headers.Location!.ToString());
    }

    // ----- seeder -------------------------------------------------------------------------------------

    private static async Task<(IOpenIddictApplicationManager Manager, IServiceScope Scope, IAMTestWebApplicationFactory Factory)> OpenAsync()
    {
        var factory = new IAMTestWebApplicationFactory();
        var scope = factory.Services.CreateScope();
        return await Task.FromResult((scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(), scope, factory));
    }

    private static (string, string?)[] ExtraClient(int i, string id, string redirect, string? logout = null)
    {
        var list = new List<(string, string?)>
        {
            ($"Seeder:ExtraPublicClients:{i}:ClientId", id),
            ($"Seeder:ExtraPublicClients:{i}:DisplayName", id + " app"),
            ($"Seeder:ExtraPublicClients:{i}:RedirectUris:0", redirect)
        };
        if (logout != null) list.Add(($"Seeder:ExtraPublicClients:{i}:PostLogoutRedirectUris:0", logout));
        return list.ToArray();
    }

    [Fact]
    public async Task ExtraPublicClients_AreCreatedWithTheMeetingClientRecipe()
    {
        var (manager, scope, factory) = await OpenAsync();
        using var _ = factory; using var __ = scope;
        var config = Config(
            ExtraClient(0, "taskmanager", "https://tasks.example.nl/api/auth/callback", "https://tasks.example.nl/")
            .Concat(ExtraClient(1, "jengo-knowledge", "https://kb.example.nl/api/auth/callback"))
            .Concat(ExtraClient(2, "jengo-meeting", "https://meet.example.nl/api/auth/callback"))
            .Concat(ExtraClient(3, "jengo-agi", "https://agi.example.nl/api/auth/callback"))
            .ToArray());

        var clients = ExtraPublicClientSeeder.Resolve(config, NullLogger.Instance);
        await ExtraPublicClientSeeder.SeedAsync(manager, clients, NullLogger.Instance, CancellationToken.None);

        foreach (var id in new[] { "taskmanager", "jengo-knowledge", "jengo-meeting", "jengo-agi" })
            Assert.NotNull(await manager.FindByClientIdAsync(id));

        var app = (await manager.FindByClientIdAsync("taskmanager"))!;
        Assert.Equal(ClientTypes.Public, await manager.GetClientTypeAsync(app));
        Assert.Equal(ConsentTypes.Implicit, await manager.GetConsentTypeAsync(app));
        Assert.Equal("taskmanager app", await manager.GetDisplayNameAsync(app));
        Assert.Equal(new[] { "https://tasks.example.nl/api/auth/callback" }, (await manager.GetRedirectUrisAsync(app)).ToArray());
        Assert.Equal(new[] { "https://tasks.example.nl/" }, (await manager.GetPostLogoutRedirectUrisAsync(app)).ToArray());
        Assert.True(await manager.HasRequirementAsync(app, Requirements.Features.ProofKeyForCodeExchange));
        Assert.True(await manager.HasPermissionAsync(app, Permissions.GrantTypes.AuthorizationCode));
        Assert.True(await manager.HasPermissionAsync(app, Permissions.GrantTypes.RefreshToken));
        Assert.True(await manager.HasPermissionAsync(app, Permissions.ResponseTypes.Code));
        foreach (var s in new[] { Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.Roles })
            Assert.True(await manager.HasPermissionAsync(app, Permissions.Prefixes.Scope + s));
        Assert.False(await manager.HasPermissionAsync(app, Permissions.GrantTypes.ClientCredentials));
    }

    [Fact]
    public async Task ExtraPublicClients_ExistingClientGetsItsRedirectUrisUpdated_NothingElseChanges()
    {
        var (manager, scope, factory) = await OpenAsync();
        using var _ = factory; using var __ = scope;

        await ExtraPublicClientSeeder.SeedAsync(manager,
            ExtraPublicClientSeeder.Resolve(Config(ExtraClient(0, "jengo-meeting", "https://old.example.nl/cb", "https://old.example.nl/")), NullLogger.Instance),
            NullLogger.Instance, CancellationToken.None);
        await ExtraPublicClientSeeder.SeedAsync(manager,
            ExtraPublicClientSeeder.Resolve(Config(ExtraClient(0, "jengo-meeting", "https://new.example.nl/cb", "https://new.example.nl/")), NullLogger.Instance),
            NullLogger.Instance, CancellationToken.None);

        var app = (await manager.FindByClientIdAsync("jengo-meeting"))!;
        Assert.Equal(new[] { "https://new.example.nl/cb" }, (await manager.GetRedirectUrisAsync(app)).ToArray());
        Assert.Equal(new[] { "https://new.example.nl/" }, (await manager.GetPostLogoutRedirectUrisAsync(app)).ToArray());
        Assert.True(await manager.HasRequirementAsync(app, Requirements.Features.ProofKeyForCodeExchange));
        Assert.Equal(1, await manager.CountAsync());
    }

    [Fact]
    public void ExtraPublicClients_JsonVariantAndEmptySlots()
    {
        var config = Config(
            ("Seeder:ExtraPublicClients:0:ClientId", ""), // empty compose slot
            ("Seeder:ExtraPublicClientsJson",
             "[{\"ClientId\":\"taskmanager\",\"RedirectUris\":[\"https://t.example.nl/cb\"],\"PostLogoutRedirectUris\":[\"https://t.example.nl/\"]}]"));

        var clients = ExtraPublicClientSeeder.Resolve(config, NullLogger.Instance);

        var c = Assert.Single(clients);
        Assert.Equal("taskmanager", c.ClientId);
        Assert.Equal(new[] { "https://t.example.nl/cb" }, c.RedirectUris);
    }

    private static async Task<HashSet<string>> ClientIdsAsync(IOpenIddictApplicationManager manager)
    {
        var ids = new HashSet<string>();
        await foreach (var app in manager.ListAsync())
            ids.Add((await manager.GetClientIdAsync(app))!);
        return ids;
    }

    [Fact]
    public async Task SkipInternalClients_SeedsOnlyReactAdminUiAndTheExtraClients()
    {
        var (manager, scope, factory) = await OpenAsync();
        using var _ = factory; using var __ = scope;
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var config = Config(new[] { ("Seeder:SkipInternalClients", (string?)"true"), ("Clients:WordPressTestProspergenics:Secret", "x") }
            .Concat(ExtraClient(0, "taskmanager", "https://tasks.example.nl/cb")).ToArray());

        await DatabaseSeeder.SeedClientsAsync(scope.ServiceProvider, config, env, CancellationToken.None);

        Assert.Equal(new HashSet<string> { "react_admin_ui", "taskmanager" }, await ClientIdsAsync(manager));
    }

    [Fact]
    public async Task SkipInternalClientsDefault_StillSeedsTheInternalClients()
    {
        var (manager, scope, factory) = await OpenAsync();
        using var _ = factory; using var __ = scope;
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var config = Config(("Clients:SquidProxy:Secret", "s"), ("Clients:OpenWebUi:Secret", "o"));

        await DatabaseSeeder.SeedClientsAsync(scope.ServiceProvider, config, env, CancellationToken.None);

        var ids = await ClientIdsAsync(manager);
        Assert.Contains("react_admin_ui", ids);
        Assert.Contains("jengo-meeting", ids);
        Assert.Contains("jengo-agi", ids);
        Assert.Contains("jengo-web", ids);
    }
}
