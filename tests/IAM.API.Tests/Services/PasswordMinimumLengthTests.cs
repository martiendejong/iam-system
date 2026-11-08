using System.Security.Cryptography;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Security;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4719: every flow that lets a person choose a password applies the shared 8-character minimum
/// (<see cref="PasswordPolicy"/>). A refused password must not consume the reset token or the invitation.
/// </summary>
public class PasswordMinimumLengthTests
{
    private const string OldPassword = "OldPassword-1!";

    private static IConfiguration JwtConfig() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
        ["Jwt:Issuer"] = "iam-tests",
        ["Jwt:Audience"] = "iam-tests",
        ["Jwt:AccessTokenExpirationMinutes"] = "5"
    }).Build();

    private static IAMDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a", false)]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("a much longer passphrase", true)]
    public void Policy_AppliesEightCharacterMinimum(string? password, bool valid)
    {
        Assert.Equal(8, PasswordPolicy.MinLength);
        Assert.Equal(valid, PasswordPolicy.IsValid(password));
        Assert.Equal(valid ? null : "Password must be at least 8 characters", PasswordPolicy.Validate(password));
    }

    // ---- reset password ----

    private static async Task<(IAMDbContext Context, AuthService Auth, User User)> ResetScene(string rawToken)
    {
        var context = NewContext();
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@reset.test",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(OldPassword, workFactor: 4),
            EmailConfirmed = true,
            IsActive = true,
            PasswordResetToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))),
            PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1)
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return (context, AuthServiceTestFactory.Create(context, JwtConfig()), user);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("1234567")]
    [InlineData(null)]
    public async Task ResetPassword_TooShort_IsRefused_TokenKept_OldPasswordStillWorks(string? shortPassword)
    {
        var (context, auth, user) = await ResetScene("reset-4719");

        Assert.False(await auth.ResetPasswordAsync("reset-4719", shortPassword!));

        var stored = (await context.Users.FindAsync(user.Id))!;
        Assert.NotNull(stored.PasswordResetToken);
        Assert.NotNull(stored.PasswordResetTokenExpiry);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, stored.PasswordHash));

        // The same link still works for a valid password afterwards.
        Assert.True(await auth.ResetPasswordAsync("reset-4719", "12345678"));
        stored = (await context.Users.FindAsync(user.Id))!;
        Assert.Null(stored.PasswordResetToken);
        Assert.True(BCrypt.Net.BCrypt.Verify("12345678", stored.PasswordHash));
    }

    [Fact]
    public async Task ResetController_TooShort_Returns400_WithPolicyMessage()
    {
        var (_, auth, _) = await ResetScene("reset-4719");
        var controller = new IAM.API.Controllers.AuthController(auth, JwtConfig());

        var response = await controller.ResetPassword(new IAM.API.Controllers.ResetPasswordRequest("reset-4719", "abc"));

        var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(response);
        Assert.Contains("Password must be at least 8 characters", bad.Value!.ToString());
    }

    // ---- invitation ----

    private static (IAMDbContext Context, InvitationService Service, Invitation Invitation, string RawToken) InviteScene(User? existing = null)
    {
        var context = NewContext();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        var role = new Role { Id = Guid.NewGuid(), Name = "Resident", Permissions = "[\"Room.View\"]" };
        var sender = new User { Email = $"{Guid.NewGuid():N}@sender.test", PasswordHash = "x", EmailConfirmed = true, IsActive = true };
        var email = existing?.Email ?? $"{Guid.NewGuid():N}@invited.test";
        var raw = Guid.NewGuid().ToString("N");
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(), Email = email, TenantId = tenant.Id, RoleId = role.Id,
            Token = TokenHasher.Sha256Hex(raw), Status = "Pending", InvitedByUserId = sender.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(3)
        };
        context.AddRange(tenant, role, sender, invitation);
        if (existing != null) context.Users.Add(existing);
        context.SaveChanges();
        var service = new InvitationService(context, new RecordingEmailService(),
            new ConfigurationBuilder().AddInMemoryCollection().Build(), NullLogger<InvitationService>.Instance, null);
        return (context, service, invitation, raw);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("1234567")]
    public async Task Accept_NewUser_ShortPassword_Refused_NoUser_InvitationStaysPending(string password)
    {
        var (context, service, invitation, raw) = InviteScene();

        var result = await service.AcceptInvitationAsync(raw, password, "A", "B");

        Assert.False(result.Success);
        Assert.Equal("Password must be at least 8 characters", result.Error);
        Assert.False(await context.Users.AnyAsync(u => u.Email == invitation.Email));
        Assert.Equal("Pending", (await context.Set<Invitation>().FindAsync(invitation.Id))!.Status);
    }

    [Fact]
    public async Task Accept_NewUser_EightCharacters_Works()
    {
        var (context, service, invitation, raw) = InviteScene();

        var result = await service.AcceptInvitationAsync(raw, "12345678", "A", "B");

        Assert.True(result.Success);
        Assert.True(await context.Users.AnyAsync(u => u.Email == invitation.Email));
    }

    [Fact]
    public async Task Accept_ExistingConfirmedUser_WithoutPassword_IsUnchanged()
    {
        var existing = new User { Email = $"{Guid.NewGuid():N}@existing.test", PasswordHash = BCrypt.Net.BCrypt.HashPassword(OldPassword, workFactor: 4), EmailConfirmed = true, IsActive = true };
        var (_, service, _, raw) = InviteScene(existing);

        var result = await service.AcceptInvitationAsync(raw, null, null, null);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Accept_ExistingUnconfirmedUser_ShortPassword_Refused_AccountUntouched()
    {
        var existing = new User { Email = $"{Guid.NewGuid():N}@existing.test", PasswordHash = BCrypt.Net.BCrypt.HashPassword(OldPassword, workFactor: 4), EmailConfirmed = false, IsActive = true };
        var (context, service, invitation, raw) = InviteScene(existing);

        var result = await service.AcceptInvitationAsync(raw, "short", null, null);

        Assert.False(result.Success);
        Assert.Equal("Password must be at least 8 characters", result.Error);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, (await context.Users.FindAsync(existing.Id))!.PasswordHash));
        Assert.Equal("Pending", (await context.Set<Invitation>().FindAsync(invitation.Id))!.Status);
    }
}
