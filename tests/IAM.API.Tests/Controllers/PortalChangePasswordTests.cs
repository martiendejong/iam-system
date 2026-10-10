using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using IAM.API.Controllers;
using IAM.API.Tests.Services;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5157: the portal password change ends the user's other sessions (refresh tokens and UserSessions),
/// keeps the calling session, and counts a wrong current password toward the login lockout.
/// </summary>
public class PortalChangePasswordTests
{
    private const string Current = "Password123!";
    private const string Fresh = "BrandNewPass456!";

    private static IAMDbContext CreateContext() => new(
        new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<User> AddUserAsync(IAMDbContext context)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Current, workFactor: 4),
            FirstName = "Port",
            LastName = "Al",
            EmailConfirmed = true,
            IsActive = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static RefreshToken AddRefreshToken(IAMDbContext context, Guid userId)
    {
        var token = new RefreshToken { UserId = userId, TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddDays(7) };
        context.RefreshTokens.Add(token);
        return token;
    }

    private static UserSession AddUserSession(IAMDbContext context, Guid userId)
    {
        var session = new UserSession { UserId = userId, SessionToken = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddDays(1) };
        context.UserSessions.Add(session);
        return session;
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        })
        .Build();

    /// <param name="refreshTokenId">The signed refresh_token_id claim of the caller's access token.</param>
    /// <param name="sessionHeaderId">The X-Session-Id request header (names a UserSession).</param>
    private static PortalController CreateController(IAMDbContext context, Guid userId, Guid? refreshTokenId = null, Guid? sessionHeaderId = null)
    {
        var controller = new PortalController(
            context, new SessionService(context, NullLogger<SessionService>.Instance), NullLogger<PortalController>.Instance);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (refreshTokenId.HasValue)
            claims.Add(new Claim("refresh_token_id", refreshTokenId.Value.ToString()));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        if (sessionHeaderId.HasValue)
            http.Request.Headers["X-Session-Id"] = sessionHeaderId.Value.ToString();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    [Fact]
    public async Task ChangePassword_RevokesOtherRefreshTokens_KeepsTheCurrentOne()
    {
        await using var context = CreateContext();
        var user = await AddUserAsync(context);
        var current = AddRefreshToken(context, user.Id);
        var other = AddRefreshToken(context, user.Id);
        var otherUsers = AddRefreshToken(context, Guid.NewGuid());
        await context.SaveChangesAsync();

        var result = await CreateController(context, user.Id, refreshTokenId: current.Id)
            .ChangePassword(new ChangePasswordRequest(Current, Fresh), default);

        Assert.IsType<OkObjectResult>(result);
        var reloaded = await context.RefreshTokens.AsNoTracking().ToDictionaryAsync(t => t.Id);
        Assert.Null(reloaded[current.Id].RevokedAt);
        Assert.NotNull(reloaded[other.Id].RevokedAt);
        Assert.Null(reloaded[otherUsers.Id].RevokedAt);
        Assert.True(BCrypt.Net.BCrypt.Verify(Fresh, (await context.Users.AsNoTracking().SingleAsync()).PasswordHash));
    }

    [Fact]
    public async Task ChangePassword_EndsOtherUserSessions_KeepsTheOneNamedByTheHeader()
    {
        await using var context = CreateContext();
        var user = await AddUserAsync(context);
        var current = AddUserSession(context, user.Id);
        var other = AddUserSession(context, user.Id);
        var otherUsers = AddUserSession(context, Guid.NewGuid());
        await context.SaveChangesAsync();

        await CreateController(context, user.Id, sessionHeaderId: current.Id)
            .ChangePassword(new ChangePasswordRequest(Current, Fresh), default);

        var sessions = await context.UserSessions.AsNoTracking().ToDictionaryAsync(s => s.Id);
        Assert.False(sessions[current.Id].IsRevoked);
        Assert.True(sessions[other.Id].IsRevoked);
        Assert.False(sessions[otherUsers.Id].IsRevoked);
    }

    [Fact]
    public async Task ChangePassword_TheSessionHeaderCannotKeepARefreshTokenAlive()
    {
        // X-Session-Id is a plain request header: naming another refresh token in it must not protect that token.
        await using var context = CreateContext();
        var user = await AddUserAsync(context);
        var current = AddRefreshToken(context, user.Id);
        var stolen = AddRefreshToken(context, user.Id);
        await context.SaveChangesAsync();

        await CreateController(context, user.Id, refreshTokenId: current.Id, sessionHeaderId: stolen.Id)
            .ChangePassword(new ChangePasswordRequest(Current, Fresh), default);

        var reloaded = await context.RefreshTokens.AsNoTracking().ToDictionaryAsync(t => t.Id);
        Assert.Null(reloaded[current.Id].RevokedAt);
        Assert.NotNull(reloaded[stolen.Id].RevokedAt);
    }

    [Fact]
    public async Task ChangePassword_ARefreshTokenIssuedBeforeNoLongerWorks_TheCallersOwnStillDoes()
    {
        await using var context = CreateContext();
        var auth = AuthServiceTestFactory.Create(context, CreateConfiguration());
        var user = await AddUserAsync(context);
        var mine = await auth.LoginAsync(user.Email, Current);
        var thiefs = await auth.LoginAsync(user.Email, Current);
        Assert.True(mine.Success && thiefs.Success);
        // The access token carries the refresh_token_id claim the controller reads (task binding).
        var myClaims = new JwtSecurityTokenHandler().ReadJwtToken(mine.AccessToken).Claims;
        var myRefreshTokenId = Guid.Parse(myClaims.Single(c => c.Type == "refresh_token_id").Value);

        var result = await CreateController(context, user.Id, refreshTokenId: myRefreshTokenId)
            .ChangePassword(new ChangePasswordRequest(Current, Fresh), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.False((await auth.RefreshTokenAsync(thiefs.RefreshToken!)).Success);
        Assert.True((await auth.RefreshTokenAsync(mine.RefreshToken!)).Success);
    }

    [Fact]
    public async Task ChangePassword_WhenCurrentSessionUnknown_EndsEverySession()
    {
        await using var context = CreateContext();
        var user = await AddUserAsync(context);
        var token = AddRefreshToken(context, user.Id);
        var session = AddUserSession(context, user.Id);
        await context.SaveChangesAsync();

        await CreateController(context, user.Id, null).ChangePassword(new ChangePasswordRequest(Current, Fresh), default);

        Assert.NotNull((await context.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == token.Id)).RevokedAt);
        Assert.True((await context.UserSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id)).IsRevoked);
    }

    [Fact]
    public async Task ChangePassword_WritesAuditLog()
    {
        await using var context = CreateContext();
        var user = await AddUserAsync(context);

        await CreateController(context, user.Id, null).ChangePassword(new ChangePasswordRequest(Current, Fresh), default);

        Assert.Contains(await context.AuditLogs.AsNoTracking().ToListAsync(), a => a.UserId == user.Id && a.Action == "PasswordChanged");
    }

    [Fact]
    public async Task WrongCurrentPassword_ChangesNothing_AndCountsAsFailedLogin()
    {
        await using var context = CreateContext();
        var user = await AddUserAsync(context);
        var token = AddRefreshToken(context, user.Id);
        await context.SaveChangesAsync();
        var hashBefore = user.PasswordHash;

        var result = await CreateController(context, user.Id, null).ChangePassword(new ChangePasswordRequest("wrong-password", Fresh), default);

        Assert.IsType<BadRequestObjectResult>(result);
        var reloaded = await context.Users.AsNoTracking().SingleAsync();
        Assert.Equal(1, reloaded.FailedLoginAttempts);
        Assert.Equal(hashBefore, reloaded.PasswordHash);
        Assert.Null((await context.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == token.Id)).RevokedAt);
    }

    [Fact]
    public async Task FiveWrongAttempts_LockTheAccount_AndTheSixthIsRefusedEvenWithTheRightPassword()
    {
        await using var context = CreateContext();
        var user = await AddUserAsync(context);
        var controller = CreateController(context, user.Id, null);

        for (var i = 0; i < 5; i++)
            Assert.IsType<BadRequestObjectResult>(await controller.ChangePassword(new ChangePasswordRequest("wrong-password", Fresh), default));

        var locked = await context.Users.AsNoTracking().SingleAsync();
        Assert.True(locked.IsLockedOut);
        Assert.True(locked.LockoutEnd > DateTime.UtcNow.AddMinutes(14));

        var sixth = await controller.ChangePassword(new ChangePasswordRequest(Current, Fresh), default);

        var refused = Assert.IsType<ObjectResult>(sixth);
        Assert.Equal(StatusCodes.Status423Locked, refused.StatusCode);
        Assert.True(BCrypt.Net.BCrypt.Verify(Current, (await context.Users.AsNoTracking().SingleAsync()).PasswordHash));
    }

    [Fact]
    public async Task FiveWrongAttempts_AtThePortal_AlsoLockTheLogin()
    {
        // Since task 5166 a wrong password at the login no longer counts (per-IP throttle instead), so the portal's
        // five tries are the only password failures that reach the persistent lock; the login still honours it.
        await using var context = CreateContext();
        var auth = AuthServiceTestFactory.Create(context, CreateConfiguration());
        var user = await AddUserAsync(context);
        var controller = CreateController(context, user.Id);

        for (var i = 0; i < 5; i++)
            await controller.ChangePassword(new ChangePasswordRequest("wrong-password", Fresh), default);

        var refused = await controller.ChangePassword(new ChangePasswordRequest(Current, Fresh), default);
        Assert.Equal(StatusCodes.Status423Locked, Assert.IsType<ObjectResult>(refused).StatusCode);
        Assert.Contains("temporarily locked", (await auth.LoginAsync(user.Email, Current)).Error);
    }

    [Fact]
    public async Task PasswordChangeIsPossibleAgain_OnceTheLockoutHasEnded()
    {
        await using var context = CreateContext();
        var user = await AddUserAsync(context);
        user.FailedLoginAttempts = 5;
        user.IsLockedOut = true;
        user.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
        await context.SaveChangesAsync();

        var result = await CreateController(context, user.Id, null).ChangePassword(new ChangePasswordRequest(Current, Fresh), default);

        Assert.IsType<OkObjectResult>(result);
        var reloaded = await context.Users.AsNoTracking().SingleAsync();
        Assert.False(reloaded.IsLockedOut);
        Assert.Null(reloaded.LockoutEnd);
    }

    [Fact]
    public async Task CorrectCurrentPassword_ResetsTheFailedAttemptCounter()
    {
        await using var context = CreateContext();
        var user = await AddUserAsync(context);
        user.FailedLoginAttempts = 4;
        await context.SaveChangesAsync();

        var result = await CreateController(context, user.Id, null).ChangePassword(new ChangePasswordRequest(Current, Fresh), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, (await context.Users.AsNoTracking().SingleAsync()).FailedLoginAttempts);
    }
}
