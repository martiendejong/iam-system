using System.Security.Cryptography;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using IAM.API.Tests.Services;

namespace IAM.API.Tests;

/// <summary>
/// Race tests for OtpService.ValidateOtpAsync against the real local Postgres (EF InMemory cannot
/// run ExecuteUpdateAsync). Every parallel request gets its own DbContext, like separate HTTP requests.
/// Rows are scoped to a unique per-run e-mail and removed afterwards.
/// </summary>
public class OtpServiceConcurrencyPostgresTests : IAsyncLifetime
{
    // Same local dev database as the telemetry Postgres tests; the password was rotated (vault 6/337),
    // so set IAM_TEST_PG_CONNECTION to the full connection string when running these locally.
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("IAM_TEST_PG_CONNECTION")
        ?? "Host=127.0.0.1;Port=5432;Database=iam_db;Username=iam_user;Password=IAM_Db_P@ss2026!";

    private const string RightCode = "123456";
    private readonly string _email = $"xunit-otp-{Guid.NewGuid():N}@example.test";

    private static IAMDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IAMDbContext>().UseNpgsql(ConnectionString).Options);

    private static OtpService NewService(IAMDbContext ctx) =>
        new(ctx, new RecordingEmailService(), new NoopSmsService(),
            Options.Create(new EmailSettings()), NullLogger<OtpService>.Instance);

    private async Task<Guid> SeedAsync(OtpPurpose purpose = OtpPurpose.Login, DateTime? expiresAt = null)
    {
        await using var ctx = NewContext();
        var otp = new OtpCode
        {
            Email = _email,
            Code = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(RightCode))),
            Purpose = purpose,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(10),
            MaxAttempts = 5
        };
        ctx.OtpCodes.Add(otp);
        await ctx.SaveChangesAsync();
        return otp.Id;
    }

    private async Task<bool> ValidateAsync(string code, OtpPurpose purpose = OtpPurpose.Login)
    {
        await using var ctx = NewContext();
        return await NewService(ctx).ValidateOtpAsync(_email, null, code, purpose);
    }

    private async Task<OtpCode> LoadAsync(Guid id)
    {
        await using var ctx = NewContext();
        return await ctx.OtpCodes.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private static async Task<bool[]> RunParallelAsync(int count, Func<Task<bool>> work)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            return await work();
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"OtpCodes\" WHERE \"Email\" = {_email}");
    }

    [Fact]
    public async Task ParallelWrongGuesses_CountExactlyFiveAttempts_ThenRightCodeIsRejected()
    {
        var id = await SeedAsync();

        var results = await RunParallelAsync(40, () => ValidateAsync("000000"));

        Assert.All(results, r => Assert.False(r));
        Assert.Equal(5, (await LoadAsync(id)).Attempts);
        Assert.False(await ValidateAsync(RightCode));
        Assert.Null((await LoadAsync(id)).UsedAt);
    }

    [Fact]
    public async Task ParallelCorrectGuesses_SucceedExactlyOnce()
    {
        var id = await SeedAsync();

        var results = await RunParallelAsync(10, () => ValidateAsync(RightCode));

        Assert.Equal(1, results.Count(r => r));
        Assert.NotNull((await LoadAsync(id)).UsedAt);
    }

    [Fact]
    public async Task Sequential_RightCodeWorks_ThenCannotBeReused()
    {
        await SeedAsync();

        Assert.True(await ValidateAsync(RightCode));
        Assert.False(await ValidateAsync(RightCode));
    }

    [Fact]
    public async Task Sequential_SixthGuessRejected_EvenIfCorrect()
    {
        await SeedAsync();

        for (var i = 0; i < 5; i++) Assert.False(await ValidateAsync("000000"));
        Assert.False(await ValidateAsync(RightCode));
    }

    [Fact]
    public async Task ExpiredCode_IsRejected()
    {
        await SeedAsync(expiresAt: DateTime.UtcNow.AddMinutes(-1));

        Assert.False(await ValidateAsync(RightCode));
    }

    [Theory]
    [InlineData(OtpPurpose.LoginTwoFactor)]
    [InlineData(OtpPurpose.MfaVerification)]
    public async Task OtherPurposes_UseSameAtomicLimit(OtpPurpose purpose)
    {
        var id = await SeedAsync(purpose);

        var results = await RunParallelAsync(20, () => ValidateAsync("000000", purpose));

        Assert.All(results, r => Assert.False(r));
        Assert.Equal(5, (await LoadAsync(id)).Attempts);
    }
}
