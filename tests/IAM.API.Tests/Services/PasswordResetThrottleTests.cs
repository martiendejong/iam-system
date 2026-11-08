using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5633: at most one password-reset mail per account while a reset token is valid, and a
/// repeat request must not overwrite the pending token.
/// </summary>
public class PasswordResetThrottleTests
{
    private readonly IAMDbContext _context;
    private readonly RecordingEmailService _email = new();
    private readonly AuthService _auth;

    public PasswordResetThrottleTests()
    {
        _context = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests"
        }).Build();
        _auth = AuthServiceTestFactory.Create(_context, configuration, _email);
    }

    private async Task<User> AddUserAsync()
    {
        var user = new User { Email = "reset@example.com", PasswordHash = "x", FirstName = "A", LastName = "B", EmailConfirmed = true, IsActive = true };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task FirstRequest_SendsOneMailAndStoresToken()
    {
        var user = await AddUserAsync();
        Assert.True(await _auth.SendPasswordResetAsync(user.Email));
        Assert.Equal(1, _email.ResetMails);
        Assert.NotNull((await _context.Users.FindAsync(user.Id))!.PasswordResetToken);
    }

    [Fact]
    public async Task RepeatRequest_SendsNothingAndLeavesTokenAndExpiryUnchanged()
    {
        var user = await AddUserAsync();
        await _auth.SendPasswordResetAsync(user.Email);
        var token = user.PasswordResetToken;
        var expiry = user.PasswordResetTokenExpiry;

        Assert.True(await _auth.SendPasswordResetAsync(user.Email));
        Assert.True(await _auth.SendPasswordResetAsync(user.Email));

        Assert.Equal(1, _email.ResetMails);
        var stored = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal(token, stored.PasswordResetToken);
        Assert.Equal(expiry, stored.PasswordResetTokenExpiry);
    }

    [Fact]
    public async Task RequestAfterExpiry_SendsAgainWithNewToken()
    {
        var user = await AddUserAsync();
        await _auth.SendPasswordResetAsync(user.Email);
        var oldToken = user.PasswordResetToken;
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddMinutes(-1);
        await _context.SaveChangesAsync();

        await _auth.SendPasswordResetAsync(user.Email);

        Assert.Equal(2, _email.ResetMails);
        Assert.NotEqual(oldToken, (await _context.Users.FindAsync(user.Id))!.PasswordResetToken);
    }

    [Fact]
    public async Task RequestAfterTokenUsed_SendsAgain()
    {
        var user = await AddUserAsync();
        await _auth.SendPasswordResetAsync(user.Email);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        await _context.SaveChangesAsync();

        await _auth.SendPasswordResetAsync(user.Email);

        Assert.Equal(2, _email.ResetMails);
    }

    [Fact]
    public async Task UnknownAddress_ReturnsTrueAndSendsNothing()
    {
        Assert.True(await _auth.SendPasswordResetAsync("nobody@example.com"));
        Assert.Equal(0, _email.ResetMails);
    }
}
