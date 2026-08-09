using System.IdentityModel.Tokens.Jwt;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 45: the admin-editable Token Configuration screen (Claims Mapping admin UI /
/// TokenConfiguration.cs) previously had no effect on real logins - AuthService and
/// SocialAuthService always issued a hardcoded 5-minute access token / 7-day refresh
/// token/cookie for every organization. These tests confirm a saved Token Configuration
/// for a user's organization (tenant) now drives login, refresh, and cookie lifetimes,
/// and that organizations which never touch the setting keep today's defaults.
/// </summary>
public class TokenLifetimeConfigurationTests
{
    private const string TestOrgLabel = "test org";

    private static IAMDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new IAMDbContext(options);
    }

    private static IConfiguration CreateConfiguration()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static (AuthService authService, IAMDbContext context) CreateAuthService()
    {
        var context = CreateContext();
        var emailService = new FakeEmailService();
        var smsService = new FakeSmsService();
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" });
        var otpService = new OtpService(context, emailService, smsService, emailSettings, NullLogger<OtpService>.Instance);
        var riskAssessmentService = new RiskAssessmentService(context, NullLogger<RiskAssessmentService>.Instance);
        var claimsMappingService = new ClaimsMappingService(context);
        var authService = new AuthService(context, CreateConfiguration(), emailService, riskAssessmentService, otpService, claimsMappingService);
        return (authService, context);
    }

    private static User CreateUser(string password = "Password123!")
    {
        return new User
        {
            Email = "user@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FirstName = "Test",
            LastName = "User",
            EmailConfirmed = true,
            IsActive = true
        };
    }

    private static int MinutesUntilExpiry(string accessToken)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        return (int)Math.Round((jwt.ValidTo - DateTime.UtcNow).TotalMinutes);
    }

    [Fact]
    public async Task LoginAsync_ForOrganizationWithSavedTokenConfiguration_UsesConfiguredAccessTokenMinutes()
    {
        var (authService, context) = CreateAuthService();
        var tenant = new Tenant { Name = TestOrgLabel, Slug = "test-org" };
        var role = new Role { Name = "Member", TenantId = tenant.Id };
        var user = CreateUser();
        context.Tenants.Add(tenant);
        context.Roles.Add(role);
        context.Users.Add(user);
        context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, TenantId = tenant.Id });
        context.TokenConfigurations.Add(new TokenConfiguration
        {
            ClientId = "react_admin_ui",
            TenantId = tenant.Id,
            AccessTokenLifetimeMinutes = 45,
            RefreshTokenLifetimeDays = 21
        });
        await context.SaveChangesAsync();

        var result = await authService.LoginAsync(user.Email, "Password123!");

        Assert.True(result.Success);
        Assert.Equal(45, result.AccessTokenLifetimeMinutes);
        Assert.NotEqual(5, result.AccessTokenLifetimeMinutes); // not the hardcoded default
        Assert.InRange(MinutesUntilExpiry(result.AccessToken!), 43, 45);
    }

    [Fact]
    public async Task LoginAsync_ForOrganizationWithSavedTokenConfiguration_UsesConfiguredRefreshTokenDays()
    {
        var (authService, context) = CreateAuthService();
        var tenant = new Tenant { Name = TestOrgLabel, Slug = "test-org" };
        var role = new Role { Name = "Member", TenantId = tenant.Id };
        var user = CreateUser();
        context.Tenants.Add(tenant);
        context.Roles.Add(role);
        context.Users.Add(user);
        context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, TenantId = tenant.Id });
        context.TokenConfigurations.Add(new TokenConfiguration
        {
            ClientId = "react_admin_ui",
            TenantId = tenant.Id,
            AccessTokenLifetimeMinutes = 45,
            RefreshTokenLifetimeDays = 21
        });
        await context.SaveChangesAsync();

        var result = await authService.LoginAsync(user.Email, "Password123!");

        Assert.True(result.Success);
        Assert.Equal(21, result.RefreshTokenLifetimeDays);
        Assert.NotEqual(7, result.RefreshTokenLifetimeDays); // not the hardcoded default

        var storedToken = await context.RefreshTokens.SingleAsync(rt => rt.UserId == user.Id);
        Assert.InRange((storedToken.ExpiresAt - DateTime.UtcNow).TotalDays, 20.9, 21.1);
    }

    [Fact]
    public async Task RefreshTokenAsync_ForOrganizationWithSavedTokenConfiguration_ReissuesWithConfiguredLifetime()
    {
        var (authService, context) = CreateAuthService();
        var tenant = new Tenant { Name = TestOrgLabel, Slug = "test-org" };
        var role = new Role { Name = "Member", TenantId = tenant.Id };
        var user = CreateUser();
        context.Tenants.Add(tenant);
        context.Roles.Add(role);
        context.Users.Add(user);
        context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, TenantId = tenant.Id });
        context.TokenConfigurations.Add(new TokenConfiguration
        {
            ClientId = "react_admin_ui",
            TenantId = tenant.Id,
            AccessTokenLifetimeMinutes = 45,
            RefreshTokenLifetimeDays = 21
        });
        await context.SaveChangesAsync();

        var loginResult = await authService.LoginAsync(user.Email, "Password123!");
        var refreshResult = await authService.RefreshTokenAsync(loginResult.RefreshToken!);

        Assert.True(refreshResult.Success);
        Assert.Equal(45, refreshResult.AccessTokenLifetimeMinutes);
        Assert.Equal(21, refreshResult.RefreshTokenLifetimeDays);
        Assert.InRange(MinutesUntilExpiry(refreshResult.AccessToken!), 43, 45);
    }

    [Fact]
    public async Task LoginAsync_ForOrganizationWithoutSavedTokenConfiguration_KeepsTodaysDefaults()
    {
        // Organization exists (user belongs to a tenant) but the admin never saved a
        // Token Configuration for it - DoD explicitly requires today's behavior here.
        var (authService, context) = CreateAuthService();
        var tenant = new Tenant { Name = "untouched org", Slug = "untouched-org" };
        var role = new Role { Name = "Member", TenantId = tenant.Id };
        var user = CreateUser();
        context.Tenants.Add(tenant);
        context.Roles.Add(role);
        context.Users.Add(user);
        context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, TenantId = tenant.Id });
        await context.SaveChangesAsync();

        var result = await authService.LoginAsync(user.Email, "Password123!");

        Assert.True(result.Success);
        Assert.Equal(5, result.AccessTokenLifetimeMinutes);
        Assert.Equal(7, result.RefreshTokenLifetimeDays);
    }

    [Fact]
    public async Task LoginAsync_ForUserWithNoTenant_KeepsTodaysDefaults()
    {
        var (authService, context) = CreateAuthService();
        var user = CreateUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await authService.LoginAsync(user.Email, "Password123!");

        Assert.True(result.Success);
        Assert.Equal(5, result.AccessTokenLifetimeMinutes);
        Assert.Equal(7, result.RefreshTokenLifetimeDays);
    }

    private class FakeEmailService : IEmailService
    {
        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
    }

    private class FakeSmsService : ISmsService
    {
        public Task<bool> SendSmsAsync(string phoneNumber, string message) => Task.FromResult(true);
    }
}
