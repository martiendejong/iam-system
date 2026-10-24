using System.Reflection;
using IAM.API.Tests.Infrastructure;
using IAM.API.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Xunit;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 4094: the startup audit must flag every OAuth client whose stored secret is a literal that has
/// been published in this repository, outside Development, and must not trip over public clients or
/// clients with a healthy secret. The audit is a private step of the (hosted, stripped-from-tests)
/// DatabaseSeeder, so it is driven by reflection against the real OpenIddict manager and store.
/// </summary>
public class DefaultClientSecretAuditTests
{
    // One literal per line so a new published default is a conscious edit here AND in DatabaseSeeder.
    private static readonly string[] PublishedDefaults =
    {
        "postman_secret_dev_only",
        "backend_secret_dev_only",
        "jengo-agi-svc-secret-dev",
        "jengo-mcp-iam-secret-dev",
        "Ow9kP2mXqR5vN8dL3jT7" // open-webui, hard-coded 2026-06-12 .. 2026-09-10
    };

    private sealed class CapturingLogger : ILogger<DatabaseSeeder>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class StubEnvironment(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "IAM.API.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = ".";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<List<(LogLevel Level, string Message)>> RunAuditAsync(
        string environmentName, params (string ClientId, string? Secret)[] clients)
    {
        using var factory = new IAMTestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        foreach (var (clientId, secret) in clients)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientType = secret is null ? OpenIddictConstants.ClientTypes.Public : OpenIddictConstants.ClientTypes.Confidential
            };
            if (secret is not null)
                descriptor.ClientSecret = secret;
            await manager.CreateAsync(descriptor);
        }

        var logger = new CapturingLogger();
        var seeder = new DatabaseSeeder(factory.Services, new ConfigurationBuilder().Build(), logger,
            new StubEnvironment(environmentName));
        var audit = typeof(DatabaseSeeder).GetMethod("AuditDefaultClientSecretsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)audit.Invoke(seeder, new object[] { scope.ServiceProvider, CancellationToken.None })!;
        return logger.Entries;
    }

    [Fact]
    public async Task Production_flags_every_published_default_and_only_those()
    {
        var clients = PublishedDefaults
            .Select((secret, i) => ($"published-{i}", (string?)secret))
            .Append(("public-client", null))
            .Append(("healthy-client", "a-random-secret-that-was-never-published-9f3b21"))
            .ToArray();

        var entries = await RunAuditAsync("Production", clients);

        var critical = entries.Where(e => e.Level == LogLevel.Critical).Select(e => e.Message).ToList();
        Assert.Equal(PublishedDefaults.Length, critical.Count);
        for (var i = 0; i < PublishedDefaults.Length; i++)
            Assert.Contains(critical, m => m.Contains($"'published-{i}'"));

        Assert.DoesNotContain(entries, e => e.Message.Contains("public-client") || e.Message.Contains("healthy-client"));
        Assert.DoesNotContain(entries, e => PublishedDefaults.Any(s => e.Message.Contains(s)));
        Assert.DoesNotContain(entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Development_reports_a_published_default_as_information_not_critical()
    {
        var entries = await RunAuditAsync("Development", ("dev-client", "postman_secret_dev_only"));

        var entry = Assert.Single(entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("'dev-client'", entry.Message);
    }
}
