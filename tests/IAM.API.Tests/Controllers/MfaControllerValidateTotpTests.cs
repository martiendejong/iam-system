using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4523: the anonymous POST /api/mfa/totp/validate endpoint must lock out an account
/// after 5 failed TOTP/recovery-code attempts, reject a correct code while still locked, and
/// fully reset FailedLoginAttempts/IsLockedOut/LockoutEnd together on a successful code -
/// matching the convention every other login success path in this codebase already follows.
/// </summary>
public class MfaControllerValidateTotpTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;
    private readonly HttpClient _superAdminClient;

    public MfaControllerValidateTotpTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _superAdminClient = factory.CreateClient();
        _superAdminClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            "admin@test.com",
            new[] { "SuperAdmin" }));
    }

    /// <summary>
    /// Creates a fresh user (via the real SuperAdmin-only create endpoint) and enrolls + activates
    /// TOTP on it (via the real authenticated setup/verify endpoints), returning the user id and
    /// the raw Base32 secret so the test can compute valid codes at will.
    /// </summary>
    private async Task<(Guid userId, string secret)> CreateTotpEnrolledUserAsync(string email)
    {
        var createResponse = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email,
            password = "Str0ngTestPassw0rd!",
            firstName = "Totp",
            lastName = "Lockout"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = created.GetProperty("id").GetGuid();

        var userClient = _factory.CreateClient();
        userClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(userId, email, new[] { "User" }));

        var setupResponse = await userClient.PostAsync("/api/mfa/totp/setup", null);
        Assert.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        var setup = await setupResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secret = setup.GetProperty("secret").GetString()!;

        var verifyResponse = await userClient.PostAsJsonAsync("/api/mfa/totp/verify", new { code = ComputeTotpCode(secret) });
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        return (userId, secret);
    }

    /// <summary>
    /// Re-implements TotpService's exact RFC 6238 (HMAC-SHA256) algorithm so tests can compute
    /// a currently-valid code from a known secret without reaching into TotpService internals.
    /// </summary>
    private static string ComputeTotpCode(string base32Secret)
    {
        var secretBytes = Base32Decode(base32Secret);
        var timeStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        var timeStepBytes = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            timeStepBytes[i] = (byte)(timeStep & 0xFF);
            timeStep >>= 8;
        }

        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(timeStepBytes);

        int offset = hash[^1] & 0x0F;
        int binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        int otp = binaryCode % 1_000_000;
        return otp.ToString().PadLeft(6, '0');
    }

    private static byte[] Base32Decode(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var normalized = base32.Trim().ToUpperInvariant().Replace("=", "").Replace(" ", "").Replace("-", "");

        var output = new List<byte>();
        int buffer = 0;
        int bitsLeft = 0;

        foreach (var c in normalized)
        {
            int value = alphabet.IndexOf(c);
            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)(buffer >> bitsLeft));
                buffer &= (1 << bitsLeft) - 1;
            }
        }

        return output.ToArray();
    }

    [Fact]
    public async Task ValidateTotp_FiveWrongCodes_LocksAccountInsteadOfJustReportingInvalidCode()
    {
        var (userId, _) = await CreateTotpEnrolledUserAsync("lockout.five.wrong@test.com");
        var anonClient = _factory.CreateClient();

        for (int attempt = 1; attempt <= 4; attempt++)
        {
            var response = await anonClient.PostAsJsonAsync("/api/mfa/totp/validate", new { userId, code = "000000" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Invalid authentication code.", body.GetProperty("error").GetString());
        }

        var fifthResponse = await anonClient.PostAsJsonAsync("/api/mfa/totp/validate", new { userId, code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, fifthResponse.StatusCode);
        var fifthBody = await fifthResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Too many failed attempts. Account temporarily locked.", fifthBody.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ValidateTotp_CorrectCodeWhileLocked_IsStillRejected()
    {
        var (userId, secret) = await CreateTotpEnrolledUserAsync("lockout.correct.while.locked@test.com");
        var anonClient = _factory.CreateClient();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            await anonClient.PostAsJsonAsync("/api/mfa/totp/validate", new { userId, code = "000000" });
        }

        var response = await anonClient.PostAsJsonAsync("/api/mfa/totp/validate", new { userId, code = ComputeTotpCode(secret) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Account is temporarily locked. Try again later.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ValidateTotp_FreshNonLockedUser_CorrectCodeSucceedsNormally()
    {
        var (userId, secret) = await CreateTotpEnrolledUserAsync("lockout.fresh.user@test.com");
        var anonClient = _factory.CreateClient();

        var response = await anonClient.PostAsJsonAsync("/api/mfa/totp/validate", new { userId, code = ComputeTotpCode(secret) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task ValidateTotp_SuccessfulCodeAfterLockoutWindowPassed_ClearsIsLockedOutAndLockoutEndToo()
    {
        // Regression test for the gap the review found: the success branch only reset
        // FailedLoginAttempts, leaving a stale IsLockedOut/LockoutEnd behind forever once an
        // account had ever been locked - unlike every other login success path in this codebase.
        var (userId, secret) = await CreateTotpEnrolledUserAsync("lockout.reset.fields@test.com");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == userId);
            user.FailedLoginAttempts = 5;
            user.IsLockedOut = true;
            user.LockoutEnd = DateTime.UtcNow.AddMinutes(-1); // window already passed
            await db.SaveChangesAsync();
        }

        var anonClient = _factory.CreateClient();
        var response = await anonClient.PostAsJsonAsync("/api/mfa/totp/validate", new { userId, code = ComputeTotpCode(secret) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == userId);
            Assert.Equal(0, user.FailedLoginAttempts);
            Assert.False(user.IsLockedOut);
            Assert.Null(user.LockoutEnd);
        }
    }

    [Fact]
    public async Task ValidateTotp_UnknownUserId_DoesNotRevealWhetherUserExists()
    {
        var anonClient = _factory.CreateClient();

        var response = await anonClient.PostAsJsonAsync("/api/mfa/totp/validate", new { userId = Guid.NewGuid(), code = "000000" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid authentication code.", body.GetProperty("error").GetString());
    }
}
