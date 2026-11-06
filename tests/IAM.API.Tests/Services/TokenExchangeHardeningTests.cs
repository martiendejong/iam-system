using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5145: the token-exchange endpoint only exchanges the caller's OWN user or service-account token, for an account
/// that is still active, with scopes no wider than the subject holds, never past the subject token's own expiry, never
/// from an already exchanged token, and never into IAM's own audience. Service-level tests use hand-built HMAC tokens
/// and an in-memory database; the endpoint tests drive the real pipeline.
/// </summary>
public class TokenExchangeHardeningTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private const string SecretKey = "DEVELOPMENT_SECRET_KEY_CHANGE_IN_PRODUCTION_32_CHARS_MIN";
    private const string Issuer = "https://localhost:5001";
    private const string IamAudience = "iam-api";
    private const string Target = "taskmanager-api";

    private readonly IAMTestWebApplicationFactory _factory;

    public TokenExchangeHardeningTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private static IAMDbContext CreateContext() => new(
        new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ServiceAccountService CreateService(IAMDbContext context) => new(context,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = SecretKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = IamAudience,
        }).Build());

    private static string BuildToken(Guid subject, string? tokenType = null, DateTime? expires = null, params string[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject.ToString()), new("sub", subject.ToString()) };
        if (tokenType != null) claims.Add(new Claim("token_type", tokenType));
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, IamAudience, claims, expires: expires ?? DateTime.UtcNow.AddHours(1), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<User> AddUserAsync(IAMDbContext context, bool active = true, string? rolePermissions = null, DateTime? roleExpiresAt = null)
    {
        var name = $"u{Guid.NewGuid():N}"[..10];
        var user = new User { Email = $"{name}@example.com", PasswordHash = "x", FirstName = name, LastName = "T", IsActive = active };
        context.Users.Add(user);
        if (rolePermissions != null)
        {
            var role = new Role { Name = $"role-{name}", Permissions = rolePermissions };
            context.Roles.Add(role);
            context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, GrantedAt = DateTime.UtcNow, ExpiresAt = roleExpiresAt });
        }
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<ServiceAccount> AddServiceAccountAsync(IAMDbContext context, bool active = true, params string[] permissions)
    {
        var account = new ServiceAccount
        {
            Name = "svc", ClientId = $"svc_{Guid.NewGuid():N}", ClientSecretHash = "h", IsActive = active,
            Permissions = JsonSerializer.Serialize(permissions),
        };
        context.ServiceAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private static JwtSecurityToken Read(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    // ---- service level ------------------------------------------------------------------------

    [Fact]
    public async Task OwnUserToken_IsExchanged_AndTheAuditRowIsWritten()
    {
        var context = CreateContext();
        var user = await AddUserAsync(context);
        var subject = BuildToken(user.Id);

        var (success, token, expiresAt) = await CreateService(context).ExchangeTokenAsync(subject, Target, user.Id);

        Assert.True(success);
        Assert.Equal(Target, Assert.Single(Read(token!).Audiences));
        Assert.True(expiresAt <= DateTime.UtcNow.AddMinutes(15).AddSeconds(5));
        var row = await context.TokenExchanges.SingleAsync();
        Assert.Equal(user.Id, row.SubjectId);
        Assert.Equal(Target, row.TargetService);
    }

    [Fact]
    public async Task SubjectTokenOfAnotherUser_IsRefused()
    {
        var context = CreateContext();
        var caller = await AddUserAsync(context);
        var victim = await AddUserAsync(context);

        var (success, token, _) = await CreateService(context).ExchangeTokenAsync(BuildToken(victim.Id), Target, caller.Id);

        Assert.False(success);
        Assert.Null(token);
        Assert.Empty(context.TokenExchanges);
    }

    [Fact]
    public async Task ServiceAccount_OwnToken_Works_AndAnotherAccountsTokenIsRefused()
    {
        var context = CreateContext();
        var mine = await AddServiceAccountAsync(context, true, "orders:read");
        var other = await AddServiceAccountAsync(context, true, "orders:read");
        var service = CreateService(context);

        Assert.True((await service.ExchangeTokenAsync(BuildToken(mine.Id, "service_account"), Target, mine.Id, "orders:read")).Success);
        Assert.False((await service.ExchangeTokenAsync(BuildToken(other.Id, "service_account"), Target, mine.Id)).Success);
        // a service account cannot exchange a USER's token either
        var user = await AddUserAsync(context);
        Assert.False((await service.ExchangeTokenAsync(BuildToken(user.Id), Target, mine.Id)).Success);
    }

    [Fact]
    public async Task DeactivatedUser_CannotExchange_EvenWithAStillValidToken()
    {
        var context = CreateContext();
        var user = await AddUserAsync(context);
        var subject = BuildToken(user.Id);
        var service = CreateService(context);
        Assert.True((await service.ExchangeTokenAsync(subject, Target, user.Id)).Success);

        user.IsActive = false;
        await context.SaveChangesAsync();

        Assert.False((await service.ExchangeTokenAsync(subject, Target, user.Id)).Success);
    }

    [Fact]
    public async Task DeactivatedServiceAccount_CannotExchange()
    {
        var context = CreateContext();
        var account = await AddServiceAccountAsync(context, active: false);

        Assert.False((await CreateService(context).ExchangeTokenAsync(BuildToken(account.Id, "service_account"), Target, account.Id)).Success);
    }

    [Fact]
    public async Task UnknownAccount_CannotExchange()
    {
        var context = CreateContext();
        var ghost = Guid.NewGuid();

        Assert.False((await CreateService(context).ExchangeTokenAsync(BuildToken(ghost), Target, ghost)).Success);
    }

    [Fact]
    public async Task ExchangedToken_CannotBeUsedAsSubjectAgain_AndNeitherCanADeviceToken()
    {
        var context = CreateContext();
        var user = await AddUserAsync(context);
        var service = CreateService(context);
        var first = await service.ExchangeTokenAsync(BuildToken(user.Id), Target, user.Id);
        Assert.True(first.Success);
        Assert.Equal("token_exchange", Read(first.AccessToken!).Claims.First(c => c.Type == "token_type").Value);

        // renewing through the exchange is closed, for the very same caller
        Assert.False((await service.ExchangeTokenAsync(first.AccessToken!, Target, user.Id)).Success);
        Assert.False((await service.ExchangeTokenAsync(BuildToken(user.Id, "device"), Target, user.Id)).Success);
        Assert.Single(context.TokenExchanges);
    }

    [Fact]
    public async Task ServiceAccountScopes_AreCappedAtItsPermissions()
    {
        var context = CreateContext();
        var account = await AddServiceAccountAsync(context, true, "orders:read", "orders:write");
        var subject = BuildToken(account.Id, "service_account");
        var service = CreateService(context);

        var ok = await service.ExchangeTokenAsync(subject, Target, account.Id, "orders:read orders:write");
        Assert.True(ok.Success);
        Assert.Equal(new[] { "orders:read", "orders:write" }, Read(ok.AccessToken!).Claims.Where(c => c.Type == "scope").Select(c => c.Value));

        Assert.False((await service.ExchangeTokenAsync(subject, Target, account.Id, "orders:read admin:everything")).Success);
        Assert.True((await service.ExchangeTokenAsync(subject, Target, account.Id)).Success); // no scope asked: nothing widened
    }

    [Fact]
    public async Task UserScopes_MustMatchAPermissionOfARoleInForce()
    {
        var context = CreateContext();
        var user = await AddUserAsync(context, rolePermissions: "[\"reports:read\"]");
        var lapsed = await AddUserAsync(context, rolePermissions: "[\"reports:read\"]", roleExpiresAt: DateTime.UtcNow.AddMinutes(-5));
        var service = CreateService(context);

        Assert.True((await service.ExchangeTokenAsync(BuildToken(user.Id), Target, user.Id, "reports:read")).Success);
        Assert.False((await service.ExchangeTokenAsync(BuildToken(user.Id), Target, user.Id, "reports:write")).Success);
        // a role whose expiry has passed grants nothing
        Assert.False((await service.ExchangeTokenAsync(BuildToken(lapsed.Id), Target, lapsed.Id, "reports:read")).Success);
        // a user with no permissions at all may still exchange without asking for a scope
        var plain = await AddUserAsync(context);
        Assert.True((await service.ExchangeTokenAsync(BuildToken(plain.Id), Target, plain.Id)).Success);
        Assert.False((await service.ExchangeTokenAsync(BuildToken(plain.Id), Target, plain.Id, "anything")).Success);
    }

    [Fact]
    public async Task Expiry_IsTheEarlierOfFifteenMinutesAndTheSubjectTokensExpiry()
    {
        var context = CreateContext();
        var user = await AddUserAsync(context);
        var service = CreateService(context);

        var shortLived = await service.ExchangeTokenAsync(BuildToken(user.Id, expires: DateTime.UtcNow.AddMinutes(5)), Target, user.Id);
        var longLived = await service.ExchangeTokenAsync(BuildToken(user.Id, expires: DateTime.UtcNow.AddHours(2)), Target, user.Id);

        Assert.True(shortLived.Success);
        Assert.InRange((shortLived.ExpiresAt!.Value - DateTime.UtcNow).TotalMinutes, 4, 5.1);
        Assert.InRange((longLived.ExpiresAt!.Value - DateTime.UtcNow).TotalMinutes, 14, 15.1);
        Assert.True(Read(shortLived.AccessToken!).ValidTo <= DateTime.UtcNow.AddMinutes(5).AddSeconds(2));
    }

    [Theory]
    [InlineData("iam-api")]
    [InlineData("IAM-API")]
    [InlineData(" iam-api ")]
    [InlineData("")]
    public async Task IamsOwnAudience_CannotBeTheTarget(string target)
    {
        var context = CreateContext();
        var user = await AddUserAsync(context);

        Assert.False((await CreateService(context).ExchangeTokenAsync(BuildToken(user.Id), target, user.Id)).Success);
        Assert.Empty(context.TokenExchanges);
    }

    // ---- endpoint -----------------------------------------------------------------------------

    private async Task<Guid> SeedUserAsync(bool active = true)
    {
        using var scope = _factory.Services.CreateScope();
        return (await AddUserAsync(scope.ServiceProvider.GetRequiredService<IAMDbContext>(), active)).Id;
    }

    private static HttpClient ClientFor(IAMTestWebApplicationFactory factory, string bearer)
    {
        var client = factory.CreateClient();
        client.AddAuthorizationHeader(bearer);
        return client;
    }

    private static object Body(string subjectToken, string resource) => new
    {
        grantType = "urn:ietf:params:oauth:grant-type:token-exchange",
        subjectToken,
        resource,
    };

    [Fact]
    public async Task Endpoint_ExchangesTheCallersOwnToken_UpToFifteenMinutes()
    {
        var userId = await SeedUserAsync();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, "u@example.com", new[] { "User" });

        var response = await ClientFor(_factory, token).PostAsJsonAsync("/api/service-accounts/exchange", Body(token, Target));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(body.GetProperty("expires_in").GetInt32(), 800, 901);
    }

    [Fact]
    public async Task Endpoint_RefusesAnotherUsersSubjectToken_WithInvalidGrant()
    {
        var callerId = await SeedUserAsync();
        var victimId = await SeedUserAsync();
        var callerToken = TestAuthenticationHelper.GenerateJwtToken(callerId, "c@example.com", new[] { "User" });
        var victimToken = TestAuthenticationHelper.GenerateJwtToken(victimId, "v@example.com", new[] { "User" });

        var response = await ClientFor(_factory, callerToken).PostAsJsonAsync("/api/service-accounts/exchange", Body(victimToken, Target));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Endpoint_RefusesADeactivatedUser_IamsOwnAudience_AndAnExchangedToken()
    {
        var userId = await SeedUserAsync();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, "u@example.com", new[] { "User" });
        var client = ClientFor(_factory, token);

        // IAM's own audience as the target
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/service-accounts/exchange", Body(token, "iam-api"))).StatusCode);

        // an exchanged token is not a valid subject token
        var first = await client.PostAsJsonAsync("/api/service-accounts/exchange", Body(token, Target));
        var exchanged = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/service-accounts/exchange", Body(exchanged, Target))).StatusCode);

        // once the account is deactivated, the same still-valid token no longer exchanges
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            (await db.Users.SingleAsync(u => u.Id == userId)).IsActive = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/service-accounts/exchange", Body(token, Target))).StatusCode);
    }
}
