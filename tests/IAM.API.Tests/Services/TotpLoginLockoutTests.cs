using System.Reflection;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Tests.Services;

public class TotpLoginLockoutTests
{
    private static IAMDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(TotpService svc, IAMDbContext ctx, User user, string secret)> SetupAsync()
    {
        var ctx = CreateContext();
        var svc = new TotpService(ctx);
        var user = new User
        {
            Email = "totp@example.com",
            PasswordHash = "x",
            FirstName = "T",
            LastName = "U",
            EmailConfirmed = true,
            IsActive = true
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        var setup = await svc.EnableTotpAsync(user.Id);
        var valid = ValidCode(svc, setup.Secret);
        Assert.True(await svc.VerifyAndActivateTotpAsync(user.Id, valid));
        return (svc, ctx, user, setup.Secret);
    }

    private static string ValidCode(TotpService svc, string secret)
    {
        var gen = typeof(TotpService).GetMethod("GenerateTotpCode", BindingFlags.NonPublic | BindingFlags.Static)!;
        var decode = typeof(TotpService).GetMethod("Base32Decode", BindingFlags.NonPublic | BindingFlags.Static)!;
        var bytes = decode.Invoke(null, new object[] { secret });
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        return (string)gen.Invoke(null, new[] { bytes, (object)step })!;
    }

    private static string WrongCode(TotpService svc, string secret)
    {
        for (var i = 0; i < 1000; i++)
        {
            var c = i.ToString("D6");
            if (!svc.ValidateCode(secret, c)) return c;
        }
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task FiveWrongTotpCodes_LockAccount_AndCorrectCodeIsThenRejected()
    {
        var (svc, ctx, user, secret) = await SetupAsync();
        var wrong = WrongCode(svc, secret);

        for (var i = 0; i < 4; i++)
            Assert.Equal(TotpLoginResult.Invalid, await svc.VerifyLoginCodeAsync(user.Id, wrong));

        Assert.Equal(TotpLoginResult.LockedOut, await svc.VerifyLoginCodeAsync(user.Id, wrong));
        Assert.Equal(TotpLoginResult.LockedOut, await svc.VerifyLoginCodeAsync(user.Id, ValidCode(svc, secret)));

        var stored = await ctx.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.True(stored.IsLockedOut);
        Assert.True(stored.LockoutEnd > DateTime.UtcNow.AddMinutes(14));
    }

    [Fact]
    public async Task WrongTotpAndWrongRecoveryCodes_ShareOneCounter()
    {
        var (svc, ctx, user, secret) = await SetupAsync();
        await svc.GenerateRecoveryCodesAsync(user.Id, 2);

        // Every attempt fails both checks but counts once; 5 attempts lock.
        for (var i = 0; i < 4; i++)
            await svc.VerifyLoginCodeAsync(user.Id, "WRONGCODE" + i);
        Assert.Equal(4, (await ctx.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).FailedLoginAttempts);
        Assert.Equal(TotpLoginResult.LockedOut, await svc.VerifyLoginCodeAsync(user.Id, WrongCode(svc, secret)));
    }

    [Fact]
    public async Task CorrectCode_ResetsCounter()
    {
        var (svc, ctx, user, secret) = await SetupAsync();
        var wrong = WrongCode(svc, secret);
        for (var i = 0; i < 3; i++)
            await svc.VerifyLoginCodeAsync(user.Id, wrong);

        Assert.Equal(TotpLoginResult.Success, await svc.VerifyLoginCodeAsync(user.Id, ValidCode(svc, secret)));
        Assert.Equal(0, (await ctx.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).FailedLoginAttempts);
    }

    [Fact]
    public async Task RecoveryCode_SucceedsAndResetsCounter()
    {
        var (svc, ctx, user, secret) = await SetupAsync();
        var codes = await svc.GenerateRecoveryCodesAsync(user.Id, 2);
        await svc.VerifyLoginCodeAsync(user.Id, WrongCode(svc, secret));

        Assert.Equal(TotpLoginResult.Success, await svc.VerifyLoginCodeAsync(user.Id, codes[0]));
        Assert.Equal(0, (await ctx.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).FailedLoginAttempts);
    }

    [Fact]
    public async Task ExpiredLockout_AllowsCorrectCodeAgain()
    {
        var (svc, ctx, user, secret) = await SetupAsync();
        var tracked = await ctx.Users.FirstAsync(u => u.Id == user.Id);
        tracked.IsLockedOut = true;
        tracked.FailedLoginAttempts = 5;
        tracked.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
        await ctx.SaveChangesAsync();

        Assert.Equal(TotpLoginResult.Success, await svc.VerifyLoginCodeAsync(user.Id, ValidCode(svc, secret)));
        Assert.False((await ctx.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).IsLockedOut);
    }

    [Fact]
    public async Task UnknownUser_IsPlainInvalid()
    {
        var svc = new TotpService(CreateContext());
        Assert.Equal(TotpLoginResult.Invalid, await svc.VerifyLoginCodeAsync(Guid.NewGuid(), "123456"));
    }
}
