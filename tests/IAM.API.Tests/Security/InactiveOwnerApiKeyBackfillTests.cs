using System.Reflection;
using Hazina.Security.ApiKeys;
using IAM.API.Tests.Infrastructure;
using IAM.API.Workers;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5224: users deactivated before task 5155 shipped still own API keys marked active. The key store blocks them
/// while the owner is inactive, but reactivating the account would bring them back, so the hosted DatabaseSeeder
/// revokes every key of an inactive owner at startup (not an EF migration: IAM never calls Migrate()). The seeder's
/// steps are private, so they are driven by reflection, like DefaultClientSecretAuditTests does for task 4094.
/// </summary>
public class InactiveOwnerApiKeyBackfillTests
{
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

    private sealed class RecordingCache : IApiKeyCache
    {
        public List<string> Invalidated { get; } = new();
        public void Invalidate(string keyHash) => Invalidated.Add(keyHash);
        public void InvalidateAll() => throw new InvalidOperationException("Only the revoked keys should be dropped from the cache");
    }

    /// <summary>The five kinds of key the clean-up has to tell apart.</summary>
    private sealed record Seeded(
        ApiKey InactiveFirst, ApiKey InactiveSecond, ApiKey InactiveAlreadyRevoked,
        ApiKey ActiveUser, ApiKey LockedOutUser, ApiKey Ownerless);

    /// <summary>One named store for the whole provider, so the seeding scope and the seeder's scope see the same data.</summary>
    private static Action<DbContextOptionsBuilder> InMemory(string databaseName) => o => o.UseInMemoryDatabase(databaseName);

    private static DatabaseSeeder CreateSeeder(IServiceProvider services, CapturingLogger logger) => new(
        services,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The seeder creates the open-webui client when missing and refuses to do so without a configured secret.
            ["Clients:OpenWebUi:Secret"] = "test-only-open-webui-secret",
        }).Build(),
        logger,
        new StubEnvironment("Development"));

    private static Task Invoke(DatabaseSeeder seeder, string method, params object[] args)
        => (Task)typeof(DatabaseSeeder).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(seeder, args)!;

    private static User NewUser(string name, bool isActive, bool isLockedOut = false) => new()
    {
        Email = $"{name}-{Guid.NewGuid():N}@example.com",
        PasswordHash = "x",
        FirstName = name,
        LastName = "Tester",
        IsActive = isActive,
        IsLockedOut = isLockedOut,
    };

    private static ApiKey NewKey(Guid? userId, bool isActive = true) => new()
    {
        Name = $"key-{Guid.NewGuid():N}"[..16],
        KeyHash = Guid.NewGuid().ToString("N"),
        KeyPrefix = "iam_test",
        Scope = "read",
        UserId = userId,
        IsActive = isActive,
    };

    private static async Task<Seeded> SeedAsync(IAMDbContext context)
    {
        var inactive = NewUser("inactive", isActive: false);
        var active = NewUser("active", isActive: true);
        // Locked out is a temporary state of an ACTIVE account and is out of scope for this clean-up.
        var lockedOut = NewUser("locked", isActive: true, isLockedOut: true);
        context.Users.AddRange(inactive, active, lockedOut);
        await context.SaveChangesAsync();

        var seeded = new Seeded(
            InactiveFirst: NewKey(inactive.Id),
            InactiveSecond: NewKey(inactive.Id),
            InactiveAlreadyRevoked: NewKey(inactive.Id, isActive: false),
            ActiveUser: NewKey(active.Id),
            LockedOutUser: NewKey(lockedOut.Id),
            Ownerless: NewKey(userId: null));
        context.ApiKeys.AddRange(seeded.InactiveFirst, seeded.InactiveSecond, seeded.InactiveAlreadyRevoked,
            seeded.ActiveUser, seeded.LockedOutUser, seeded.Ownerless);
        await context.SaveChangesAsync();
        return seeded;
    }

    private static async Task<Dictionary<Guid, bool>> ReadActiveFlagsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await context.ApiKeys.AsNoTracking().ToDictionaryAsync(k => k.Id, k => k.IsActive);
    }

    private static void AssertOnlyInactiveOwnersKeysRevoked(Seeded keys, IReadOnlyDictionary<Guid, bool> active)
    {
        Assert.False(active[keys.InactiveFirst.Id]);
        Assert.False(active[keys.InactiveSecond.Id]);
        Assert.False(active[keys.InactiveAlreadyRevoked.Id]); // was revoked before and stays that way
        Assert.True(active[keys.ActiveUser.Id]);
        Assert.True(active[keys.LockedOutUser.Id]);
        Assert.True(active[keys.Ownerless.Id]);
    }

    private static string BackfillLine(CapturingLogger logger)
        => Assert.Single(logger.Entries, e => e.Message.StartsWith("API key backfill:", StringComparison.Ordinal)).Message;

    [Fact]
    public async Task Startup_RevokesEveryKeyOfInactiveUsers_LeavesTheRestAlone_AndASecondStartChangesNothing()
    {
        using var factory = new IAMTestWebApplicationFactory();
        Seeded keys;
        using (var scope = factory.Services.CreateScope())
            keys = await SeedAsync(scope.ServiceProvider.GetRequiredService<IAMDbContext>());

        var first = new CapturingLogger();
        await Invoke(CreateSeeder(factory.Services, first), "InitializeDatabaseAsync", CancellationToken.None);

        AssertOnlyInactiveOwnersKeysRevoked(keys, await ReadActiveFlagsAsync(factory.Services));
        Assert.Equal("API key backfill: 2 active key(s) owned by inactive users revoked", BackfillLine(first));
        Assert.DoesNotContain(first.Entries, e => e.Level >= LogLevel.Error);

        // A second start finds nothing left to do and reports 0.
        var second = new CapturingLogger();
        await Invoke(CreateSeeder(factory.Services, second), "InitializeDatabaseAsync", CancellationToken.None);

        AssertOnlyInactiveOwnersKeysRevoked(keys, await ReadActiveFlagsAsync(factory.Services));
        Assert.Equal("API key backfill: 0 active key(s) owned by inactive users revoked", BackfillLine(second));
    }

    [Fact]
    public async Task Backfill_DropsOnlyTheRevokedKeysFromTheKeyCache()
    {
        var cache = new RecordingCache();
        var services = new ServiceCollection()
            .AddDbContext<IAMDbContext>(InMemory(Guid.NewGuid().ToString()))
            .AddSingleton<IApiKeyCache>(cache)
            .BuildServiceProvider();
        Seeded keys;
        using (var scope = services.CreateScope())
            keys = await SeedAsync(scope.ServiceProvider.GetRequiredService<IAMDbContext>());

        using (var scope = services.CreateScope())
            await Invoke(CreateSeeder(services, new CapturingLogger()), "BackfillApiKeysOfInactiveUsersAsync",
                scope.ServiceProvider, CancellationToken.None);

        // The module caches validated keys for a couple of minutes: the revoked ones must leave it at once.
        Assert.Equal(new[] { keys.InactiveFirst.KeyHash, keys.InactiveSecond.KeyHash }.Order(), cache.Invalidated.Order());
        AssertOnlyInactiveOwnersKeysRevoked(keys, await ReadActiveFlagsAsync(services));
    }

    [Fact]
    public async Task Backfill_WorksWithoutAKeyCacheRegistered()
    {
        var services = new ServiceCollection()
            .AddDbContext<IAMDbContext>(InMemory(Guid.NewGuid().ToString()))
            .BuildServiceProvider();
        Seeded keys;
        using (var scope = services.CreateScope())
            keys = await SeedAsync(scope.ServiceProvider.GetRequiredService<IAMDbContext>());

        var logger = new CapturingLogger();
        using (var scope = services.CreateScope())
            await Invoke(CreateSeeder(services, logger), "BackfillApiKeysOfInactiveUsersAsync",
                scope.ServiceProvider, CancellationToken.None);

        AssertOnlyInactiveOwnersKeysRevoked(keys, await ReadActiveFlagsAsync(services));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Backfill_FailureIsLoggedAndNeverBlocksStartup()
    {
        var services = new ServiceCollection()
            .AddScoped<IAMDbContext>(_ => throw new InvalidOperationException("database unavailable"))
            .BuildServiceProvider();
        var logger = new CapturingLogger();

        using var scope = services.CreateScope();
        await Invoke(CreateSeeder(services, logger), "BackfillApiKeysOfInactiveUsersAsync",
            scope.ServiceProvider, CancellationToken.None); // must not throw

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("API key backfill", StringComparison.Ordinal));
    }
}
