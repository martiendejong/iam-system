using Hazina.Security.ApiKeys;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5155: the one helper every deactivation route uses to revoke the keys its user owns. The routes themselves are
/// covered end to end in ApiKeyAuthenticationTests; this pins down what the helper revokes and what it leaves alone.
/// </summary>
public class ApiKeyRevocationTests
{
    private static IAMDbContext CreateContext() => new(
        new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<User> AddUserAsync(IAMDbContext context, string name)
    {
        var user = new User { Email = $"{name}@example.com", PasswordHash = "x", FirstName = name, LastName = "Tester", IsActive = true };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<ApiKey> AddKeyAsync(IAMDbContext context, Guid? userId, bool isActive = true)
    {
        var key = new ApiKey
        {
            Name = $"key-{Guid.NewGuid():N}"[..16],
            KeyHash = Guid.NewGuid().ToString("N"),
            KeyPrefix = "iam_test",
            Scope = "read",
            UserId = userId,
            IsActive = isActive,
        };
        context.ApiKeys.Add(key);
        await context.SaveChangesAsync();
        return key;
    }

    private sealed class RecordingCache : IApiKeyCache
    {
        public List<string> Invalidated { get; } = new();
        public void Invalidate(string keyHash) => Invalidated.Add(keyHash);
        public void InvalidateAll() => throw new InvalidOperationException("Only the revoked keys should be dropped from the cache");
    }

    [Fact]
    public async Task RevokeApiKeys_RevokesEveryLiveKeyOfTheUser_AndReturnsTheirHashes()
    {
        var context = CreateContext();
        var user = await AddUserAsync(context, "leaver");
        var first = await AddKeyAsync(context, user.Id);
        var second = await AddKeyAsync(context, user.Id);

        var revoked = await context.RevokeApiKeysAsync(user.Id);
        await context.SaveChangesAsync();

        Assert.Equal(new[] { first.KeyHash, second.KeyHash }.Order(), revoked.Order());
        Assert.False((await context.ApiKeys.FindAsync(first.Id))!.IsActive);
        Assert.False((await context.ApiKeys.FindAsync(second.Id))!.IsActive);
    }

    [Fact]
    public async Task RevokeApiKeys_LeavesOtherUsersKeysAndOwnerlessKeysAlone()
    {
        var context = CreateContext();
        var leaver = await AddUserAsync(context, "leaver");
        var stays = await AddUserAsync(context, "stays");
        var theirs = await AddKeyAsync(context, stays.Id);
        var serviceKey = await AddKeyAsync(context, userId: null);
        await AddKeyAsync(context, leaver.Id);

        await context.RevokeApiKeysAsync(leaver.Id);
        await context.SaveChangesAsync();

        Assert.True((await context.ApiKeys.FindAsync(theirs.Id))!.IsActive);
        Assert.True((await context.ApiKeys.FindAsync(serviceKey.Id))!.IsActive);
    }

    [Fact]
    public async Task RevokeApiKeys_ReportsOnlyKeysItActuallyRevoked()
    {
        var context = CreateContext();
        var user = await AddUserAsync(context, "leaver");
        var live = await AddKeyAsync(context, user.Id);
        await AddKeyAsync(context, user.Id, isActive: false);

        var revoked = await context.RevokeApiKeysAsync(user.Id);

        Assert.Equal(new[] { live.KeyHash }, revoked);
    }

    [Fact]
    public async Task RevokeApiKeys_ForSeveralUsers_RevokesAllOfThem_AndNothingForAnEmptyList()
    {
        var context = CreateContext();
        var a = await AddUserAsync(context, "a");
        var b = await AddUserAsync(context, "b");
        var c = await AddUserAsync(context, "c");
        await AddKeyAsync(context, a.Id);
        await AddKeyAsync(context, b.Id);
        var keep = await AddKeyAsync(context, c.Id);

        Assert.Empty(await context.RevokeApiKeysAsync(Array.Empty<Guid>()));
        var revoked = await context.RevokeApiKeysAsync(new[] { a.Id, b.Id, a.Id });
        await context.SaveChangesAsync();

        Assert.Equal(2, revoked.Count);
        Assert.Equal(1, await context.ApiKeys.CountAsync(k => k.IsActive));
        Assert.True((await context.ApiKeys.FindAsync(keep.Id))!.IsActive);
    }

    [Fact]
    public async Task RevokeApiKeys_OnlyStagesTheChange_TheCallersSaveCommitsIt()
    {
        var context = CreateContext();
        var user = await AddUserAsync(context, "leaver");
        var key = await AddKeyAsync(context, user.Id);

        await context.RevokeApiKeysAsync(user.Id);

        // Not saved yet: a second context on the same store still sees a live key, so the revocation commits (or rolls back)
        // together with the deactivation the caller is making.
        Assert.True(context.ChangeTracker.HasChanges());
        Assert.True((await context.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == key.Id)).IsActive);
    }

    [Fact]
    public void Forget_DropsOnlyTheGivenKeysFromTheCache_AndIsHarmlessWithoutOne()
    {
        var cache = new RecordingCache();

        cache.Forget(new[] { "hash-1", "hash-2" });
        ((IApiKeyCache?)null).Forget(new[] { "hash-3" });

        Assert.Equal(new[] { "hash-1", "hash-2" }, cache.Invalidated);
    }
}
