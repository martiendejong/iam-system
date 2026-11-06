using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5491: authenticator secrets are encrypted at rest (vault "enc:" entries), legacy plain-text secrets keep
/// working and are converted by a repeatable migration, and a code is accepted once (its time step, and anything
/// older, is refused afterwards).
/// </summary>
public class TotpEncryptionAndReplayTests
{
    private const int StepSeconds = 30;

    private sealed class Rig
    {
        public required IAMDbContext Db { get; init; }
        public required SecretsVaultService Vault { get; init; }
        public required TotpService Service { get; init; }
    }

    private static Rig CreateRig()
    {
        var db = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SecretsVault:MasterKey"] = Convert.ToBase64String(new byte[32])
        }).Build();
        var vault = new SecretsVaultService(db, config, NullLogger<SecretsVaultService>.Instance);
        return new Rig { Db = db, Vault = vault, Service = new TotpService(db, vault, NullLogger<TotpService>.Instance) };
    }

    private static long CurrentStep() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;

    private static string CodeFor(string base32Secret, long step) =>
        TotpService.GenerateTotpCode(TotpService.Base32Decode(base32Secret), step);

    private static async Task<User> AddUserAsync(IAMDbContext db, string? storedSecret, bool enabled)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "T",
            LastName = "U",
            PasswordHash = "x",
            TwoFactorEnabled = enabled,
            TwoFactorMethod = enabled ? TwoFactorMethod.Totp : TwoFactorMethod.None,
            TwoFactorSecret = storedSecret
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static string NewPlainSecret() =>
        TotpService.Base32Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));

    [Fact]
    public async Task Enrolment_StoresEncryptedSecret_ThatIsNotBase32_AndStillActivates()
    {
        var rig = CreateRig();
        var user = await AddUserAsync(rig.Db, null, enabled: false);

        var setup = await rig.Service.EnableTotpAsync(user.Id);

        var stored = (await rig.Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).TwoFactorSecret!;
        Assert.StartsWith("enc:", stored);
        Assert.DoesNotContain(setup.Secret, stored);
        Assert.Throws<FormatException>(() => TotpService.Base32Decode(stored)); // ':' is not base32
        var vaultValue = await rig.Vault.GetSecretValueAsync(Guid.Parse(stored["enc:".Length..]));
        Assert.Equal(setup.Secret, vaultValue);

        Assert.True(await rig.Service.VerifyAndActivateTotpAsync(user.Id, CodeFor(setup.Secret, CurrentStep())));
        Assert.True((await rig.Db.Users.SingleAsync(u => u.Id == user.Id)).TwoFactorEnabled);
    }

    [Fact]
    public async Task LegacyPlaintextSecret_KeepsWorking_ForLogin()
    {
        var rig = CreateRig();
        var secret = NewPlainSecret();
        var user = await AddUserAsync(rig.Db, secret, enabled: true);

        Assert.True(await rig.Service.ValidateTotpLoginAsync(user.Id, CodeFor(secret, CurrentStep())));
    }

    [Fact]
    public async Task Migration_EncryptsLegacySecrets_IsRepeatable_AndUsersStayEnrolled()
    {
        var rig = CreateRig();
        var secret = NewPlainSecret();
        var legacy = await AddUserAsync(rig.Db, secret, enabled: true);
        var already = await AddUserAsync(rig.Db, "enc:" + Guid.NewGuid(), enabled: true);
        var none = await AddUserAsync(rig.Db, null, enabled: false);

        Assert.Equal(1, await rig.Service.EncryptLegacySecretsAsync());
        Assert.Equal(0, await rig.Service.EncryptLegacySecretsAsync());

        var stored = (await rig.Db.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id)).TwoFactorSecret!;
        Assert.StartsWith("enc:", stored);
        Assert.NotEqual(secret, stored);
        Assert.True((await rig.Db.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id)).TwoFactorEnabled);
        Assert.StartsWith("enc:", (await rig.Db.Users.AsNoTracking().SingleAsync(u => u.Id == already.Id)).TwoFactorSecret!);
        Assert.Null((await rig.Db.Users.AsNoTracking().SingleAsync(u => u.Id == none.Id)).TwoFactorSecret);

        Assert.True(await rig.Service.ValidateTotpLoginAsync(legacy.Id, CodeFor(secret, CurrentStep())));
    }

    [Fact]
    public async Task SameCode_IsRefusedTheSecondTime_EvenInsideTheSkewWindow()
    {
        var rig = CreateRig();
        var secret = NewPlainSecret();
        var user = await AddUserAsync(rig.Db, secret, enabled: true);
        var code = CodeFor(secret, CurrentStep());

        Assert.True(await rig.Service.ValidateTotpLoginAsync(user.Id, code));
        Assert.False(await rig.Service.ValidateTotpLoginAsync(user.Id, code));
    }

    [Fact]
    public async Task OlderStep_IsRefused_AfterANewerStepWasAccepted()
    {
        var rig = CreateRig();
        var secret = NewPlainSecret();
        var user = await AddUserAsync(rig.Db, secret, enabled: true);
        var step = CurrentStep();

        Assert.True(await rig.Service.ValidateTotpLoginAsync(user.Id, CodeFor(secret, step + 1)));
        Assert.False(await rig.Service.ValidateTotpLoginAsync(user.Id, CodeFor(secret, step)));
        Assert.False(await rig.Service.ValidateTotpLoginAsync(user.Id, CodeFor(secret, step - 1)));
    }

    [Fact]
    public async Task WrongCode_DoesNotBurnTheStep()
    {
        var rig = CreateRig();
        var secret = NewPlainSecret();
        var user = await AddUserAsync(rig.Db, secret, enabled: true);
        var good = CodeFor(secret, CurrentStep());
        var bad = good == "000000" ? "000001" : "000000";

        Assert.False(await rig.Service.ValidateTotpLoginAsync(user.Id, bad));
        Assert.True(await rig.Service.ValidateTotpLoginAsync(user.Id, good));
    }

    [Fact]
    public async Task Disable_AcceptsCorrectCode_ClearsSecretAndRemovesVaultEntry()
    {
        var rig = CreateRig();
        var user = await AddUserAsync(rig.Db, null, enabled: false);
        var setup = await rig.Service.EnableTotpAsync(user.Id);
        var stored = (await rig.Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).TwoFactorSecret!;
        var step = CurrentStep();
        Assert.True(await rig.Service.VerifyAndActivateTotpAsync(user.Id, CodeFor(setup.Secret, step)));

        // Activation burned this step, so disabling needs the next one.
        Assert.False(await rig.Service.DisableTotpAsync(user.Id, CodeFor(setup.Secret, step)));
        Assert.True(await rig.Service.DisableTotpAsync(user.Id, CodeFor(setup.Secret, step + 1)));

        var after = await rig.Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Null(after.TwoFactorSecret);
        Assert.False(after.TwoFactorEnabled);
        Assert.Null(await rig.Vault.GetSecretValueAsync(Guid.Parse(stored["enc:".Length..])));
    }

    [Fact]
    public async Task ConcurrentUseOfOneCode_AcceptsOnlyOneRequest_OnPostgres()
    {
        // EF InMemory cannot run the atomic ExecuteUpdate path, so this runs only against a real Postgres that
        // already has the "TwoFactorLastUsedStep" column (docs/manual-migrations). Set IAM_TEST_PG_CONNECTION.
        var conn = Environment.GetEnvironmentVariable("IAM_TEST_PG_CONNECTION");
        if (string.IsNullOrWhiteSpace(conn))
            return;

        IAMDbContext NewDb() => new(new DbContextOptionsBuilder<IAMDbContext>().UseNpgsql(conn).Options);
        var secret = NewPlainSecret();
        Guid userId;
        await using (var seed = NewDb())
            userId = (await AddUserAsync(seed, secret, enabled: true)).Id;

        try
        {
            var code = CodeFor(secret, CurrentStep());
            var gate = new TaskCompletionSource();
            var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                await using var db = NewDb();
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SecretsVault:MasterKey"] = Convert.ToBase64String(new byte[32])
                }).Build();
                var vault = new SecretsVaultService(db, config, NullLogger<SecretsVaultService>.Instance);
                await gate.Task;
                return await new TotpService(db, vault).ValidateTotpLoginAsync(userId, code);
            })).ToArray();
            gate.SetResult();

            Assert.Equal(1, (await Task.WhenAll(tasks)).Count(r => r));
        }
        finally
        {
            await using var cleanup = NewDb();
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Users\" WHERE \"Id\" = {userId}");
        }
    }
}
