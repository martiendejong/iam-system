using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5156: a time-boxed role (UserRole.ExpiresAt) used to be written into every JWT the service issued
/// even after it had lapsed - the database-backed authorizers honoured the expiry, the tokens did not, so a
/// temporary SuperAdmin or app admin role never really ended. These tests drive the real AuthService
/// (password login, passwordless login, refresh) and ClaimsMappingService against an in-memory database,
/// decode the JWT and assert that only roles with no expiry or a future expiry are issued.
/// </summary>
public class ExpiredRoleTokenClaimsTests
{
    private const string Password = "Password123!";

    private static IAMDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        }).Build();

    private static (AuthService auth, IAMDbContext context) CreateAuthService()
    {
        var context = CreateContext();
        return (AuthServiceTestFactory.Create(context, CreateConfiguration()), context);
    }

    private static User NewUser() => new()
    {
        Email = "temp-admin@example.com",
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
        FirstName = "Temp",
        LastName = "Admin",
        EmailConfirmed = true,
        IsActive = true
    };

    private static Role NewRole(string name, Guid? tenantId = null) => new() { Name = name, TenantId = tenantId };

    private static UserRole Assign(User user, Role role, DateTime? expiresAt, Guid? tenantId = null) =>
        new() { UserId = user.Id, RoleId = role.Id, TenantId = tenantId, ExpiresAt = expiresAt };

    private static IReadOnlyList<string> RolesOf(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims
            .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
            .Select(c => c.Value)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

    private static async Task<(AuthService auth, IAMDbContext context, User user)> SeedUserWithMixedRolesAsync()
    {
        var (auth, context) = CreateAuthService();
        var user = NewUser();
        var forever = NewRole("Auditor");
        var later = NewRole("Reviewer");
        var lapsedSuper = NewRole("SuperAdmin");
        var lapsedApp = NewRole("taskmanager:admin");
        context.Users.Add(user);
        context.Roles.AddRange(forever, later, lapsedSuper, lapsedApp);
        context.UserRoles.AddRange(
            Assign(user, forever, null),
            Assign(user, later, DateTime.UtcNow.AddDays(30)),
            Assign(user, lapsedSuper, DateTime.UtcNow.AddMinutes(-1)),
            Assign(user, lapsedApp, DateTime.UtcNow.AddDays(-2)));
        await context.SaveChangesAsync();
        return (auth, context, user);
    }

    // ----- every JWT login path ----------------------------------------------------------------

    [Fact]
    public async Task PasswordLogin_IssuesOnlyRolesWithNoExpiryOrAFutureExpiry()
    {
        var (auth, _, user) = await SeedUserWithMixedRolesAsync();

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.True(result.Success);
        Assert.Equal(new[] { "Auditor", "Reviewer" }, RolesOf(result.AccessToken!));
    }

    [Fact]
    public async Task PasswordLogin_UserWhoseOnlyRoleExpired_GetsNoRoleClaimAtAll()
    {
        var (auth, context) = CreateAuthService();
        var user = NewUser();
        var role = NewRole("SuperAdmin");
        context.Users.Add(user);
        context.Roles.Add(role);
        context.UserRoles.Add(Assign(user, role, DateTime.UtcNow.AddHours(-1)));
        await context.SaveChangesAsync();

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.True(result.Success);
        Assert.Empty(RolesOf(result.AccessToken!));
    }

    [Fact]
    public async Task PasswordlessLogin_OtpPasskeySocialAndMagicLinkPath_AlsoDropsExpiredRoles()
    {
        // OTP, passkey, social/SSO and magic-link sign-in all finish through CompletePasswordlessLoginAsync /
        // LoginBypassPasswordAsync; both must issue the same filtered roles as the password login.
        var (auth, _, user) = await SeedUserWithMixedRolesAsync();

        var passwordless = await auth.CompletePasswordlessLoginAsync(user);
        var bypass = await auth.LoginBypassPasswordAsync(user);

        Assert.True(passwordless.Success);
        Assert.Equal(new[] { "Auditor", "Reviewer" }, RolesOf(passwordless.AccessToken!));
        Assert.True(bypass.Success);
        Assert.Equal(new[] { "Auditor", "Reviewer" }, RolesOf(bypass.AccessToken!));
    }

    [Fact]
    public async Task Refresh_RoleThatLapsedSinceLogin_IsGoneFromTheRefreshedToken()
    {
        var (auth, context) = CreateAuthService();
        var user = NewUser();
        var always = NewRole("Auditor");
        var temporary = NewRole("SuperAdmin");
        context.Users.Add(user);
        context.Roles.AddRange(always, temporary);
        var temporaryAssignment = Assign(user, temporary, DateTime.UtcNow.AddHours(1));
        context.UserRoles.AddRange(Assign(user, always, null), temporaryAssignment);
        await context.SaveChangesAsync();

        var login = await auth.LoginAsync(user.Email, Password);
        Assert.Equal(new[] { "Auditor", "SuperAdmin" }, RolesOf(login.AccessToken!)); // valid at login

        temporaryAssignment.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);                // lapses afterwards
        await context.SaveChangesAsync();

        var refreshed = await auth.RefreshTokenAsync(login.RefreshToken!);

        Assert.True(refreshed.Success);
        Assert.Equal(new[] { "Auditor" }, RolesOf(refreshed.AccessToken!));
    }

    [Fact]
    public async Task RoleWithNoExpiryOrAFutureExpiry_IsIssuedExactlyAsBefore_AtLoginAndRefresh()
    {
        var (auth, context) = CreateAuthService();
        var user = NewUser();
        var forever = NewRole("Auditor");
        var later = NewRole("SuperAdmin");
        context.Users.Add(user);
        context.Roles.AddRange(forever, later);
        context.UserRoles.AddRange(Assign(user, forever, null), Assign(user, later, DateTime.UtcNow.AddDays(90)));
        await context.SaveChangesAsync();

        var login = await auth.LoginAsync(user.Email, Password);
        var refreshed = await auth.RefreshTokenAsync(login.RefreshToken!);

        Assert.Equal(new[] { "Auditor", "SuperAdmin" }, RolesOf(login.AccessToken!));
        Assert.Equal(new[] { "Auditor", "SuperAdmin" }, RolesOf(refreshed.AccessToken!));
    }

    // ----- claims mapping (token preview + organization lookup) --------------------------------

    [Fact]
    public async Task TokenPreview_ShowsOnlyTheRolesARealTokenWouldCarry()
    {
        var (_, context, user) = await SeedUserWithMixedRolesAsync();
        var claimsMapping = new ClaimsMappingService(context);

        var preview = await claimsMapping.PreviewTokenAsync("react_admin_ui", user.Id);

        var roles = preview.Claims.Where(c => c.Type == "role").Select(c => c.Value).OrderBy(r => r, StringComparer.Ordinal);
        Assert.Equal(new[] { "Auditor", "Reviewer" }, roles);
    }

    [Fact]
    public async Task RolePermissionMappingRule_DoesNotFireForAnExpiredRole()
    {
        var (_, context, user) = await SeedUserWithMixedRolesAsync();
        context.ClaimsMappingRules.AddRange(
            new ClaimsMappingRule { ClientId = "react_admin_ui", SourceType = ClaimSourceType.RolePermission, SourcePath = "SuperAdmin", TargetClaim = "is_super", IsActive = true },
            new ClaimsMappingRule { ClientId = "react_admin_ui", SourceType = ClaimSourceType.RolePermission, SourcePath = "Auditor", TargetClaim = "is_auditor", IsActive = true });
        await context.SaveChangesAsync();
        var claimsMapping = new ClaimsMappingService(context);

        var preview = await claimsMapping.PreviewTokenAsync("react_admin_ui", user.Id);

        Assert.DoesNotContain(preview.Claims, c => c.Type == "is_super");
        Assert.Contains(preview.Claims, c => c.Type == "is_auditor");
    }

    [Fact]
    public async Task TokenLifetimeLookup_IgnoresTheTenantOfAnExpiredRole()
    {
        var (_, context) = CreateAuthService();
        var user = NewUser();
        var role = NewRole("Member");
        var lapsedTenant = new Tenant { Name = "Lapsed Org", Slug = "lapsed-org" };
        var currentTenant = new Tenant { Name = "Current Org", Slug = "current-org" };
        context.Users.Add(user);
        context.Roles.Add(role);
        context.Tenants.AddRange(lapsedTenant, currentTenant);
        context.UserRoles.AddRange(
            Assign(user, role, DateTime.UtcNow.AddDays(-1), lapsedTenant.Id),
            Assign(user, role, null, currentTenant.Id));
        context.TokenConfigurations.AddRange(
            new TokenConfiguration { ClientId = "react_admin_ui", TenantId = lapsedTenant.Id, AccessTokenLifetimeMinutes = 55, RefreshTokenLifetimeDays = 30 },
            new TokenConfiguration { ClientId = "react_admin_ui", TenantId = currentTenant.Id, AccessTokenLifetimeMinutes = 10, RefreshTokenLifetimeDays = 2 });
        await context.SaveChangesAsync();

        var lifetime = await new ClaimsMappingService(context).ResolveTokenLifetimeForUserAsync(user.Id);

        Assert.NotNull(lifetime);
        Assert.Equal(10, lifetime!.AccessTokenLifetimeMinutes);
        Assert.Equal(2, lifetime.RefreshTokenLifetimeDays);
    }

    [Fact]
    public async Task TokenLifetimeLookup_UserWithOnlyAnExpiredTenantRole_HasNoOrganization()
    {
        var (_, context) = CreateAuthService();
        var user = NewUser();
        var role = NewRole("Member");
        var tenant = new Tenant { Name = "Lapsed Org", Slug = "lapsed-org" };
        context.Users.Add(user);
        context.Roles.Add(role);
        context.Tenants.Add(tenant);
        context.UserRoles.Add(Assign(user, role, DateTime.UtcNow.AddDays(-1), tenant.Id));
        context.TokenConfigurations.Add(new TokenConfiguration { ClientId = "react_admin_ui", TenantId = tenant.Id, AccessTokenLifetimeMinutes = 55, RefreshTokenLifetimeDays = 30 });
        await context.SaveChangesAsync();

        var lifetime = await new ClaimsMappingService(context).ResolveTokenLifetimeForUserAsync(user.Id);

        Assert.Null(lifetime);
    }
}
