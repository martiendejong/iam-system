using System.Text.Json;
using Fido2NetLib;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5489: passkey challenges live in a thread-safe, expiring, single-use store keyed by the challenge, and
/// authenticate/begin answers identically for unknown, passkey-less and real passkey users.
/// </summary>
public class PasskeyChallengeTests
{
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PasskeyChallengeStore NewStore(FakeClock? clock = null) =>
        new(new MemoryCache(new MemoryCacheOptions()), clock ?? new FakeClock());

    private static byte[] NewChallenge() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Take_IsSingleUse()
    {
        var store = NewStore();
        var c = NewChallenge();
        var user = Guid.NewGuid();
        store.Put(PasskeyChallengeKind.Authentication, c, user);
        var key = PasskeyChallengeStore.ToBase64Url(c);

        var first = store.Take(PasskeyChallengeKind.Authentication, key);
        Assert.NotNull(first);
        Assert.Equal(user, first!.UserId);
        Assert.Null(store.Take(PasskeyChallengeKind.Authentication, key));
    }

    [Fact]
    public void Take_RejectsExpiredChallenge()
    {
        var clock = new FakeClock();
        var store = NewStore(clock);
        var c = NewChallenge();
        store.Put(PasskeyChallengeKind.Registration, c, Guid.NewGuid());

        clock.Now = clock.Now.Add(PasskeyChallengeStore.Lifetime).AddSeconds(1);

        Assert.Null(store.Take(PasskeyChallengeKind.Registration, PasskeyChallengeStore.ToBase64Url(c)));
    }

    [Fact]
    public void Take_AcceptsChallengeJustBeforeExpiry()
    {
        var clock = new FakeClock();
        var store = NewStore(clock);
        var c = NewChallenge();
        store.Put(PasskeyChallengeKind.Registration, c, Guid.NewGuid());
        clock.Now = clock.Now.Add(PasskeyChallengeStore.Lifetime).AddSeconds(-1);

        Assert.NotNull(store.Take(PasskeyChallengeKind.Registration, PasskeyChallengeStore.ToBase64Url(c)));
    }

    [Fact]
    public void Kinds_DoNotCrossOver()
    {
        var store = NewStore();
        var c = NewChallenge();
        store.Put(PasskeyChallengeKind.Registration, c, Guid.NewGuid());

        Assert.Null(store.Take(PasskeyChallengeKind.Authentication, PasskeyChallengeStore.ToBase64Url(c)));
    }

    [Fact]
    public void TwoPutsForTheSameUser_DoNotOverwriteEachOther()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        var a = NewChallenge();
        var b = NewChallenge();
        store.Put(PasskeyChallengeKind.Authentication, a, user);
        store.Put(PasskeyChallengeKind.Authentication, b, user);

        Assert.NotNull(store.Take(PasskeyChallengeKind.Authentication, PasskeyChallengeStore.ToBase64Url(a)));
        Assert.NotNull(store.Take(PasskeyChallengeKind.Authentication, PasskeyChallengeStore.ToBase64Url(b)));
    }

    [Fact]
    public async Task ConcurrentPutsAndTakes_AreSafeAndEachChallengeIsTakenExactlyOnce()
    {
        var store = NewStore();
        var challenges = Enumerable.Range(0, 2000).Select(_ => NewChallenge()).ToList();

        await Task.WhenAll(challenges.Select(c => Task.Run(() =>
            store.Put(PasskeyChallengeKind.Authentication, c, Guid.NewGuid()))));

        var takes = await Task.WhenAll(challenges
            .SelectMany(c => new[] { c, c })
            .Select(c => Task.Run(() =>
                store.Take(PasskeyChallengeKind.Authentication, PasskeyChallengeStore.ToBase64Url(c)) != null)));

        Assert.Equal(challenges.Count, takes.Count(t => t));
    }

    // --- authenticate/begin uniformity ------------------------------------------------------

    private static (PasskeyService Service, IAMDbContext Context, PasskeyChallengeStore Store) NewService()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var context = new IAMDbContext(options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "unit-test-secret-key-unit-test-secret-key"
        }).Build();
        var fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "IAM",
            Origins = new HashSet<string> { "https://localhost" }
        });
        var store = NewStore();
        return (new PasskeyService(context, fido2, config, store), context, store);
    }

    private static User AddUser(IAMDbContext context, string email)
    {
        var user = new User
        {
            Email = email,
            PasswordHash = "x",
            FirstName = "A",
            LastName = "B",
            EmailConfirmed = true,
            IsActive = true
        };
        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }

    private static string[] Keys(AssertionOptions o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

    [Fact]
    public async Task Begin_IsUniformForUnknownPasskeylessAndRealUsers()
    {
        var (service, context, _) = NewService();
        var passkeyless = AddUser(context, "nopasskey@example.com");
        var real = AddUser(context, "real@example.com");
        context.Credentials.Add(new Credential
        {
            Id = Guid.NewGuid(),
            UserId = real.Id,
            CredentialId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
            PublicKey = new byte[] { 9, 9, 9 },
            CredType = "public-key",
            AaGuid = Guid.Empty,
            CreatedAt = DateTime.UtcNow
        });
        context.SaveChanges();

        var unknown = await service.BeginAuthenticationAsync("ghost@example.com");
        var noPasskey = await service.BeginAuthenticationAsync(passkeyless.Email);
        var realOpts = await service.BeginAuthenticationAsync(real.Email);

        Assert.Equal(Keys(realOpts), Keys(unknown));
        Assert.Equal(Keys(realOpts), Keys(noPasskey));
        Assert.Single(unknown.AllowCredentials);
        Assert.Single(noPasskey.AllowCredentials);
        Assert.Single(realOpts.AllowCredentials);
    }

    [Fact]
    public async Task Begin_DecoyIsStablePerEmailAndDiffersBetweenEmails()
    {
        var (service, _, _) = NewService();

        var a1 = await service.BeginAuthenticationAsync("a@example.com");
        var a2 = await service.BeginAuthenticationAsync("a@example.com");
        var b = await service.BeginAuthenticationAsync("b@example.com");

        Assert.Equal(a1.AllowCredentials[0].Id, a2.AllowCredentials[0].Id);
        Assert.NotEqual(a1.AllowCredentials[0].Id, b.AllowCredentials[0].Id);
        Assert.NotEqual(a1.Challenge, a2.Challenge);
    }

    [Fact]
    public async Task Complete_ForDecoyChallenge_FailsAndBurnsTheChallenge()
    {
        var (service, _, store) = NewService();
        var options = await service.BeginAuthenticationAsync("ghost@example.com");
        var key = PasskeyChallengeStore.ToBase64Url(options.Challenge);

        var clientData = System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { type = "webauthn.get", challenge = key, origin = "https://localhost" }));
        var response = new AuthenticatorAssertionRawResponse
        {
            Id = PasskeyChallengeStore.ToBase64Url(options.AllowCredentials[0].Id),
            RawId = options.AllowCredentials[0].Id,
            Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                ClientDataJson = clientData,
                AuthenticatorData = new byte[37],
                Signature = new byte[64]
            }
        };

        Assert.Null(await service.CompleteAuthenticationAsync(response));
        Assert.Null(store.Take(PasskeyChallengeKind.Authentication, key));
    }
}
