using IAM.API.Tests.Infrastructure;
using IAM.API.Workers;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5804: how WordPress sites get registered at IAM. test.prospergenics.com is built in; every other site
/// is a WordPressSso:Sites configuration entry (no code change), and nothing is ever registered without a secret.
/// </summary>
public class WordPressSsoSeederTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    private static (string, string?)[] Site(int index, string clientId, string siteUrl, string? secret = "s3cret", string? displayName = null)
    {
        var list = new List<(string, string?)>
        {
            ($"WordPressSso:Sites:{index}:ClientId", clientId),
            ($"WordPressSso:Sites:{index}:SiteUrl", siteUrl),
            ($"WordPressSso:Sites:{index}:Secret", secret)
        };
        if (displayName != null) list.Add(($"WordPressSso:Sites:{index}:DisplayName", displayName));
        return list.ToArray();
    }

    // ----- ResolveSites -------------------------------------------------------------------------------

    [Fact]
    public void TheTestSiteIsBuiltIn_AndItsSecretComesFromItsConfigKey()
    {
        var sites = WordPressSsoSeeder.ResolveSites(Config((WordPressSsoSeeder.TestSiteSecretConfigKey, "abc")), new CapturingLogger());

        var site = Assert.Single(sites);
        Assert.Equal("wordpress-test-prospergenics", site.ClientId);
        Assert.Equal("abc", site.Secret);
        Assert.Equal("https://test.prospergenics.com/wp-login.php?action=jengo-iam-callback", site.RedirectUri);
        Assert.Equal("https://test.prospergenics.com/", site.PostLogoutRedirectUri);
        Assert.Equal("app:wordpress-test-prospergenics", site.RoleCategory);
    }

    [Fact]
    public void AnotherSiteIsJustAConfigurationEntry()
    {
        var sites = WordPressSsoSeeder.ResolveSites(
            Config(Site(0, "wordpress-karibumara", "https://karibumara.com", "k-secret", "WordPress (karibumara.com)")), new CapturingLogger());

        Assert.Equal(2, sites.Count);
        var site = sites[1];
        Assert.Equal("wordpress-karibumara", site.ClientId);
        Assert.Equal("WordPress (karibumara.com)", site.DisplayName);
        Assert.Equal("k-secret", site.Secret);
        Assert.Equal("https://karibumara.com/wp-login.php?action=jengo-iam-callback", site.RedirectUri);
        Assert.Equal("https://karibumara.com/", site.PostLogoutRedirectUri);
    }

    [Fact]
    public void ASiteInASubdirectoryKeepsItsPath_AndTheDisplayNameDefaultsToTheHost()
    {
        var sites = WordPressSsoSeeder.ResolveSites(Config(Site(0, "wordpress-blog", "https://example.com/blog/")), new CapturingLogger());

        Assert.Equal("https://example.com/blog/wp-login.php?action=jengo-iam-callback", sites[1].RedirectUri);
        Assert.Equal("https://example.com/blog/", sites[1].PostLogoutRedirectUri);
        Assert.Equal("WordPress (example.com)", sites[1].DisplayName);
    }

    [Theory]
    [InlineData("Wordpress_Bad", "https://ok.example.com/")]          // not a valid client id
    [InlineData("ab", "https://ok.example.com/")]                      // too short
    [InlineData("wordpress-http", "http://ok.example.com/")]           // not https
    [InlineData("wordpress-query", "https://ok.example.com/?x=1")]     // query string
    [InlineData("wordpress-fragment", "https://ok.example.com/#x")]    // fragment
    [InlineData("wordpress-relative", "/just/a/path")]                 // not absolute
    [InlineData("wordpress-empty", "")]                                // no url
    public void InvalidEntriesAreSkippedWithAWarning(string clientId, string siteUrl)
    {
        var logger = new CapturingLogger();

        var sites = WordPressSsoSeeder.ResolveSites(Config(Site(0, clientId, siteUrl)), logger);

        Assert.Single(sites); // only the built-in site
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void ADuplicateClientIdIsSkipped_IncludingOneThatShadowsTheBuiltInSite()
    {
        var logger = new CapturingLogger();
        var values = Site(0, "wordpress-twice", "https://one.example.com/")
            .Concat(Site(1, "wordpress-twice", "https://two.example.com/"))
            .Concat(Site(2, "wordpress-test-prospergenics", "https://evil.example.com/"))
            .ToArray();

        var sites = WordPressSsoSeeder.ResolveSites(Config(values), logger);

        Assert.Equal(2, sites.Count);
        Assert.Equal("https://one.example.com/", sites[1].SiteUrl);
        Assert.Equal("https://test.prospergenics.com/", sites[0].SiteUrl);
    }

    // ----- clients --------------------------------------------------------------------------------------

    private static async Task<(IOpenIddictApplicationManager Manager, IServiceScope Scope, IAMTestWebApplicationFactory Factory)> OpenAsync()
    {
        var factory = new IAMTestWebApplicationFactory();
        var scope = factory.Services.CreateScope();
        return (scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(), scope, factory);
    }

    [Fact]
    public async Task TheClientIsRegisteredAsAConfidentialPkceClientWithExactlyTheSitesUris()
    {
        var (manager, scope, factory) = await OpenAsync();
        using var _ = factory; using var __ = scope;
        var logger = new CapturingLogger();
        var sites = WordPressSsoSeeder.ResolveSites(Config((WordPressSsoSeeder.TestSiteSecretConfigKey, "abc")), logger);

        await WordPressSsoSeeder.SeedClientsAsync(manager, sites, logger, CancellationToken.None);

        var app = await manager.FindByClientIdAsync("wordpress-test-prospergenics");
        Assert.NotNull(app);
        Assert.Equal(ClientTypes.Confidential, await manager.GetClientTypeAsync(app));
        Assert.Equal(new[] { "https://test.prospergenics.com/wp-login.php?action=jengo-iam-callback" },
            (await manager.GetRedirectUrisAsync(app)).ToArray());
        Assert.Equal(new[] { "https://test.prospergenics.com/" }, (await manager.GetPostLogoutRedirectUrisAsync(app)).ToArray());
        Assert.True(await manager.HasRequirementAsync(app, Requirements.Features.ProofKeyForCodeExchange));
        Assert.True(await manager.HasPermissionAsync(app, Permissions.Endpoints.Authorization));
        Assert.True(await manager.HasPermissionAsync(app, Permissions.Endpoints.Token));
        Assert.True(await manager.HasPermissionAsync(app, Permissions.Endpoints.EndSession));
        Assert.True(await manager.HasPermissionAsync(app, Permissions.GrantTypes.AuthorizationCode));
        Assert.True(await manager.HasPermissionAsync(app, Permissions.ResponseTypes.Code));
        Assert.True(await manager.ValidateClientSecretAsync(app, "abc"));
    }

    [Fact]
    public async Task TheClientGetsNoMoreGrantsThanTheLoginNeeds()
    {
        var (manager, scope, factory) = await OpenAsync();
        using var _ = factory; using var __ = scope;
        var logger = new CapturingLogger();
        var sites = WordPressSsoSeeder.ResolveSites(Config((WordPressSsoSeeder.TestSiteSecretConfigKey, "abc")), logger);

        await WordPressSsoSeeder.SeedClientsAsync(manager, sites, logger, CancellationToken.None);

        var app = (await manager.FindByClientIdAsync("wordpress-test-prospergenics"))!;
        Assert.False(await manager.HasPermissionAsync(app, Permissions.GrantTypes.RefreshToken));
        Assert.False(await manager.HasPermissionAsync(app, Permissions.GrantTypes.ClientCredentials));
        Assert.False(await manager.HasPermissionAsync(app, Permissions.Endpoints.Introspection));
        Assert.False(await manager.HasPermissionAsync(app, Permissions.Endpoints.Revocation));
        Assert.False(await manager.HasPermissionAsync(app, Permissions.GrantTypes.Implicit));
    }

    [Fact]
    public async Task ASiteWithoutASecretIsNotRegistered_AndTheWarningNamesTheConfigKey()
    {
        var (manager, scope, factory) = await OpenAsync();
        using var _ = factory; using var __ = scope;
        var logger = new CapturingLogger();
        var sites = WordPressSsoSeeder.ResolveSites(Config(Site(0, "wordpress-nosecret", "https://nosecret.example.com/", secret: null)), logger);

        await WordPressSsoSeeder.SeedClientsAsync(manager, sites, logger, CancellationToken.None);

        Assert.Null(await manager.FindByClientIdAsync("wordpress-test-prospergenics"));
        Assert.Null(await manager.FindByClientIdAsync("wordpress-nosecret"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(WordPressSsoSeeder.TestSiteSecretConfigKey));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("wordpress-nosecret"));
    }

    [Fact]
    public async Task AConfiguredSiteIsRegisteredWithItsOwnSecretAndUris()
    {
        var (manager, scope, factory) = await OpenAsync();
        using var _ = factory; using var __ = scope;
        var logger = new CapturingLogger();
        var sites = WordPressSsoSeeder.ResolveSites(Config(Site(0, "wordpress-karibumara", "https://karibumara.com/", "kb-secret")), logger);

        await WordPressSsoSeeder.SeedClientsAsync(manager, sites, logger, CancellationToken.None);

        var app = await manager.FindByClientIdAsync("wordpress-karibumara");
        Assert.NotNull(app);
        Assert.True(await manager.ValidateClientSecretAsync(app, "kb-secret"));
        Assert.Equal("https://karibumara.com/wp-login.php?action=jengo-iam-callback", (await manager.GetRedirectUrisAsync(app)).Single());
        Assert.Null(await manager.FindByClientIdAsync("wordpress-test-prospergenics")); // built-in has no secret here
    }

    [Fact]
    public async Task SeedingTwiceChangesNothing_AndNeverOverwritesAnExistingClientsSecret()
    {
        var (manager, scope, factory) = await OpenAsync();
        using var _ = factory; using var __ = scope;
        var logger = new CapturingLogger();

        await WordPressSsoSeeder.SeedClientsAsync(manager,
            WordPressSsoSeeder.ResolveSites(Config((WordPressSsoSeeder.TestSiteSecretConfigKey, "first")), logger), logger, CancellationToken.None);
        await WordPressSsoSeeder.SeedClientsAsync(manager,
            WordPressSsoSeeder.ResolveSites(Config((WordPressSsoSeeder.TestSiteSecretConfigKey, "second")), logger), logger, CancellationToken.None);

        var app = (await manager.FindByClientIdAsync("wordpress-test-prospergenics"))!;
        Assert.True(await manager.ValidateClientSecretAsync(app, "first"));
        Assert.False(await manager.ValidateClientSecretAsync(app, "second"));
        Assert.Equal(1, await manager.CountAsync());
    }

    // ----- role catalog ------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRoleCatalogIsSeededPerSite_AsGlobalAppRoles()
    {
        using var factory = new IAMTestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var logger = new CapturingLogger();
        var sites = WordPressSsoSeeder.ResolveSites(Config(Site(0, "wordpress-karibumara", "https://karibumara.com/")), logger);

        await WordPressSsoSeeder.SeedAppRolesAsync(db, sites, logger, CancellationToken.None);

        foreach (var clientId in new[] { "wordpress-test-prospergenics", "wordpress-karibumara" })
        {
            var roles = await db.Roles.Where(r => r.Category == $"app:{clientId}").OrderBy(r => r.Name).ToListAsync();
            Assert.Equal(
                new[] { "administrator", "author", "editor", "subscriber" }.Select(r => $"{clientId}:{r}").ToArray(),
                roles.Select(r => r.Name).ToArray());
            Assert.All(roles, r => { Assert.Null(r.TenantId); Assert.False(r.IsSystemRole); });
        }
    }

    [Fact]
    public async Task SeedingTheCatalogTwiceAddsNothing_AndLeavesExistingRolesAlone()
    {
        using var factory = new IAMTestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var logger = new CapturingLogger();
        var sites = WordPressSsoSeeder.ResolveSites(Config(), logger);
        db.Roles.Add(new Role
        {
            Name = "wordpress-test-prospergenics:editor", Category = "app:wordpress-test-prospergenics",
            Description = "customised by an admin", TenantId = null, IsSystemRole = false
        });
        await db.SaveChangesAsync();

        await WordPressSsoSeeder.SeedAppRolesAsync(db, sites, logger, CancellationToken.None);
        await WordPressSsoSeeder.SeedAppRolesAsync(db, sites, logger, CancellationToken.None);

        var roles = await db.Roles.Where(r => r.Category == "app:wordpress-test-prospergenics").ToListAsync();
        Assert.Equal(4, roles.Count);
        Assert.Equal("customised by an admin", roles.Single(r => r.Name == "wordpress-test-prospergenics:editor").Description);
    }
}
