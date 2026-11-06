using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAM.API.Tests.Services;

/// <summary>Task 5570: the tenant IP allowlist is enforced at sign-in, passwordless sign-in and refresh.</summary>
public class SignInNetworkGateTests
{
    private const string Password = "Password123!";
    private const string Allowed = "203.0.113.10";
    private const string Blocked = "198.51.100.7";

    private static IAMDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Microsoft.Extensions.Configuration.IConfiguration Config() =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
                ["Jwt:Issuer"] = "iam-tests",
                ["Jwt:Audience"] = "iam-tests",
                ["Jwt:AccessTokenExpirationMinutes"] = "5"
            }).Build();

    private static async Task<Tenant> AddTenantAsync(IAMDbContext c, string slug, params string[] allowlist)
    {
        var tenant = new Tenant { Name = slug, Slug = slug };
        c.Tenants.Add(tenant);
        foreach (var cidr in allowlist)
            c.IpAllowlistEntries.Add(new IpAllowlistEntry { TenantId = tenant.Id, Cidr = cidr });
        await c.SaveChangesAsync();
        return tenant;
    }

    private static async Task<User> AddUserAsync(IAMDbContext c, params Tenant[] memberOf)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = "T",
            LastName = "U",
            EmailConfirmed = true,
            IsActive = true
        };
        c.Users.Add(user);
        foreach (var t in memberOf)
        {
            var role = new Role { Name = $"member-{Guid.NewGuid():N}", TenantId = t.Id };
            c.Roles.Add(role);
            c.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, TenantId = t.Id });
        }
        await c.SaveChangesAsync();
        return user;
    }

    private static AuthService Auth(IAMDbContext c) => AuthServiceTestFactory.Create(c, Config());

    [Fact]
    public async Task Password_login_from_listed_address_works_and_from_other_address_is_refused_and_logged()
    {
        var c = CreateContext();
        var tenant = await AddTenantAsync(c, "acme", "203.0.113.0/24");
        var user = await AddUserAsync(c, tenant);
        var auth = Auth(c);

        var ok = await auth.LoginAsync(user.Email, Password, Allowed);
        Assert.True(ok.Success);
        Assert.NotNull(ok.AccessToken);

        var refused = await auth.LoginAsync(user.Email, Password, Blocked);
        Assert.False(refused.Success);
        Assert.True(refused.NetworkBlocked);
        Assert.Null(refused.AccessToken);
        Assert.Equal(ISignInNetworkGate.RefusalMessage, refused.Error);

        var log = await c.BlockedIpLogs.SingleAsync();
        Assert.Equal(tenant.Id, log.TenantId);
        Assert.Equal(Blocked, log.IpAddress);
        Assert.Contains(c.AuditLogs, a => a.Action == "SignInBlockedByIpAllowlist" && a.UserId == user.Id);
    }

    [Fact]
    public async Task Wrong_password_from_blocked_address_still_looks_like_wrong_password()
    {
        var c = CreateContext();
        var tenant = await AddTenantAsync(c, "acme", "203.0.113.0/24");
        var user = await AddUserAsync(c, tenant);

        var result = await Auth(c).LoginAsync(user.Email, "nope", Blocked);

        Assert.False(result.NetworkBlocked);
        Assert.Equal(AuthService.InvalidCredentialsMessage, result.Error);
        Assert.Empty(c.BlockedIpLogs);
    }

    [Fact]
    public async Task Tenant_without_allowlist_entries_changes_nothing()
    {
        var c = CreateContext();
        var tenant = await AddTenantAsync(c, "open");
        var user = await AddUserAsync(c, tenant);

        var result = await Auth(c).LoginAsync(user.Email, Password, Blocked);

        Assert.True(result.Success);
        Assert.Empty(c.BlockedIpLogs);
    }

    [Fact]
    public async Task Inactive_allowlist_entries_are_ignored()
    {
        var c = CreateContext();
        var tenant = await AddTenantAsync(c, "acme", "203.0.113.0/24");
        foreach (var e in c.IpAllowlistEntries) e.IsActive = false;
        await c.SaveChangesAsync();
        var user = await AddUserAsync(c, tenant);

        Assert.True((await Auth(c).LoginAsync(user.Email, Password, Blocked)).Success);
    }

    [Fact]
    public async Task Missing_address_never_matches_an_active_list()
    {
        var c = CreateContext();
        var tenant = await AddTenantAsync(c, "acme", "203.0.113.0/24");
        var user = await AddUserAsync(c, tenant);

        var result = await Auth(c).LoginAsync(user.Email, Password, ipAddress: null);

        Assert.True(result.NetworkBlocked);
    }

    [Fact]
    public async Task Multi_tenant_user_must_satisfy_every_restricting_tenant_and_open_tenants_do_not_count()
    {
        var c = CreateContext();
        var a = await AddTenantAsync(c, "a", "203.0.113.0/24");
        var b = await AddTenantAsync(c, "b", "192.0.2.0/24");
        var open = await AddTenantAsync(c, "open");
        var both = await AddUserAsync(c, a, b, open);
        var onlyA = await AddUserAsync(c, a, open);
        var auth = Auth(c);

        Assert.True((await auth.LoginAsync(both.Email, Password, Allowed)).NetworkBlocked);
        Assert.True((await auth.LoginAsync(onlyA.Email, Password, Allowed)).Success);
        Assert.True((await auth.LoginAsync(onlyA.Email, Password, Blocked)).NetworkBlocked);
        Assert.Equal(b.Id, c.BlockedIpLogs.Single(l => l.IpAddress == Allowed).TenantId);
    }

    [Fact]
    public async Task A_restriction_in_a_tenant_the_user_does_not_belong_to_does_not_apply()
    {
        var c = CreateContext();
        await AddTenantAsync(c, "other", "192.0.2.0/24");
        var mine = await AddTenantAsync(c, "mine");
        var user = await AddUserAsync(c, mine);

        Assert.True((await Auth(c).LoginAsync(user.Email, Password, Blocked)).Success);
    }

    [Fact]
    public async Task Passwordless_login_covers_otp_magic_link_passkey_and_social()
    {
        var c = CreateContext();
        var tenant = await AddTenantAsync(c, "acme", "203.0.113.0/24");
        var user = await AddUserAsync(c, tenant);
        var auth = Auth(c);

        var refused = await auth.CompletePasswordlessLoginAsync(user, Blocked, "ua");
        Assert.True(refused.NetworkBlocked);
        Assert.Null(refused.AccessToken);

        var ok = await auth.CompletePasswordlessLoginAsync(user, Allowed, "ua");
        Assert.True(ok.Success);
        Assert.NotNull(ok.AccessToken);
    }

    [Fact]
    public async Task Refresh_from_blocked_address_is_refused_and_keeps_the_token_usable_from_a_listed_one()
    {
        var c = CreateContext();
        var tenant = await AddTenantAsync(c, "acme", "203.0.113.0/24");
        var user = await AddUserAsync(c, tenant);
        var auth = Auth(c);
        var login = await auth.LoginAsync(user.Email, Password, Allowed);
        Assert.True(login.Success);

        var refused = await auth.RefreshTokenAsync(login.RefreshToken!, Blocked);
        Assert.False(refused.Success);
        Assert.True(refused.NetworkBlocked);

        var renewed = await auth.RefreshTokenAsync(login.RefreshToken!, Allowed);
        Assert.True(renewed.Success);
        Assert.NotNull(renewed.AccessToken);
    }

    [Fact]
    public async Task Gate_with_explicit_tenant_only_applies_that_tenants_list()
    {
        var c = CreateContext();
        var a = await AddTenantAsync(c, "a", "203.0.113.0/24");
        var b = await AddTenantAsync(c, "b", "192.0.2.0/24");
        var user = await AddUserAsync(c, a, b);
        var gate = new SignInNetworkGate(c, NullLogger<SignInNetworkGate>.Instance);

        Assert.True(await gate.IsAllowedAsync(user.Id, Allowed, tenantId: a.Id));
        Assert.False(await gate.IsAllowedAsync(user.Id, Allowed, tenantId: b.Id));
    }

    [Theory]
    [InlineData("203.0.113.10", "203.0.113.0/24", true)]
    [InlineData("203.0.114.10", "203.0.113.0/24", false)]
    [InlineData("203.0.113.10", "203.0.113.10/32", true)]
    [InlineData("::ffff:203.0.113.10", "203.0.113.0/24", true)]
    [InlineData("2001:db8::5", "2001:db8::/32", true)]
    [InlineData("2001:db9::5", "2001:db8::/32", false)]
    [InlineData("2001:db8::5", "203.0.113.0/24", false)]
    [InlineData("not-an-ip", "203.0.113.0/24", false)]
    [InlineData(null, "203.0.113.0/24", false)]
    public void CidrMatcher_matches_v4_v6_and_mapped_addresses(string? ip, string cidr, bool expected) =>
        Assert.Equal(expected, CidrMatcher.Contains(ip, cidr));
}
