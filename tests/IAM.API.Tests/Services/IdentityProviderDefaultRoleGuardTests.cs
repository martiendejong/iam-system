using System.Net;
using System.Reflection;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4697: the privileged-role rule and its enforcement INSIDE the service (not only in the
/// controller), plus the login-time guard for provider rows that were written before the rule existed.
/// </summary>
public class IdentityProviderDefaultRoleGuardTests
{
    private static readonly Guid TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly IdentityProviderActor GlobalAdmin = new(Guid.NewGuid(), true, null);
    private static readonly IdentityProviderActor PlainUser = new(Guid.NewGuid(), false, null);

    private static IAMDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new IAMDbContext(options);
    }

    private static SocialAuthService CreateService(IAMDbContext context, HttpMessageHandler? handler = null, IMemoryCache? cache = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5",
            ["SocialAuth:RedirectUri"] = "https://app.example.com/callback"
        }).Build();

        return new SocialAuthService(
            context,
            config,
            new FakeHttpClientFactory(handler ?? new FakeGoogleHandler("unused-sub", "unused@example.com")),
            new FakeSecretsVaultService(),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            AuthServiceTestFactory.Create(context, config));
    }

    private static Role AddRole(IAMDbContext context, string name, string permissions = "[\"Room.View\"]", Guid? tenantId = null)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = name, Permissions = permissions, TenantId = tenantId };
        context.Roles.Add(role);
        context.SaveChanges();
        return role;
    }

    private static IdentityProvider NewProvider(Guid? defaultRoleId, Guid? tenantId = null, bool autoCreate = true) => new()
    {
        Name = "google",
        DisplayName = "Google",
        Type = IdentityProviderType.Google,
        TenantId = tenantId,
        ClientId = "client-id",
        ClientSecret = "client-secret",
        IsActive = true,
        AutoCreateUsers = autoCreate,
        DefaultRoleId = defaultRoleId
    };

    // ----- PrivilegedRoles --------------------------------------------------------------------

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("superadmin")]
    [InlineData("  SystemAdmin  ")]
    [InlineData("SecurityAdmin")]
    [InlineData("ComplianceOfficer")]
    [InlineData("TenantAdmin")]
    [InlineData("EmergencyAccess")]
    [InlineData("BuildingOwner")]
    [InlineData("BuildingManager")]
    [InlineData("OrganizationOwner")]
    [InlineData("Admin")]
    [InlineData("Contoso-Admin")]
    [InlineData("TenantAdministrator")]
    public void IsPrivileged_TrueForAdministrativeRoleNames(string name)
    {
        Assert.True(PrivilegedRoles.IsPrivileged(new Role { Name = name, Permissions = "[]" }));
    }

    [Theory]
    [InlineData("[\"*\"]")]
    [InlineData("[\"Room.View\",\"*\"]")]
    [InlineData("[\"Building.*\"]")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    [InlineData("[null]")]
    public void IsPrivileged_TrueForWildcardOrUnreadablePermissions(string permissions)
    {
        Assert.True(PrivilegedRoles.IsPrivileged(new Role { Name = "PowerUser", Permissions = permissions }));
    }

    [Theory]
    [InlineData("Resident", "[\"Room.View\",\"Device.ViewOwn\",\"Amenity.Book\",\"Maintenance.Request\"]")]
    [InlineData("Contractor", "[\"Building.View\",\"Maintenance.Perform\"]")]
    [InlineData("RealEstateAgent", "[\"Property.List\"]")]
    [InlineData("User", "[]")]
    [InlineData("Member", "")]
    public void IsPrivileged_FalseForOrdinaryRoles(string name, string permissions)
    {
        Assert.False(PrivilegedRoles.IsPrivileged(new Role { Name = name, Permissions = permissions }));
    }

    [Fact]
    public void PrivilegedRoles_CoverEveryRoleTheApiAuthorizesOn()
    {
        // Any role that unlocks an [Authorize(Roles = ...)] endpoint is privileged by definition. When
        // someone adds a new role to an attribute, this fails until it is added to PrivilegedRoles.Names.
        var authorizedRoles = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => new MemberInfo[] { t }.Concat(t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            .SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>(inherit: false))
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
            .SelectMany(a => a.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(authorizedRoles);
        var missing = authorizedRoles.Where(r => !PrivilegedRoles.Names.Contains(r)).ToList();
        Assert.True(missing.Count == 0, "Roles used in [Authorize(Roles)] but not in PrivilegedRoles.Names: " + string.Join(", ", missing));
    }

    // ----- service enforces the rules itself --------------------------------------------------

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    [InlineData("TenantAdmin")]
    public async Task Create_ThrowsValidation_ForPrivilegedDefaultRole(string roleName)
    {
        using var context = CreateContext();
        var role = AddRole(context, roleName);
        var service = CreateService(context);

        await Assert.ThrowsAsync<IdentityProviderValidationException>(() =>
            service.CreateIdentityProviderAsync(NewProvider(role.Id), GlobalAdmin));

        Assert.Empty(context.IdentityProviders);
    }

    [Fact]
    public async Task Create_ThrowsValidation_ForUnknownDefaultRole()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<IdentityProviderValidationException>(() =>
            service.CreateIdentityProviderAsync(NewProvider(Guid.NewGuid()), GlobalAdmin));
    }

    [Fact]
    public async Task Create_StoresProvider_ForOrdinaryDefaultRole()
    {
        using var context = CreateContext();
        var role = AddRole(context, "Resident");
        var service = CreateService(context);

        var created = await service.CreateIdentityProviderAsync(NewProvider(role.Id), GlobalAdmin);

        Assert.Equal(role.Id, created.DefaultRoleId);
        Assert.Single(context.IdentityProviders);
    }

    [Fact]
    public async Task Update_ThrowsValidation_ForPrivilegedDefaultRole_AndLeavesProviderUntouched()
    {
        using var context = CreateContext();
        var safe = AddRole(context, "Resident");
        var privileged = AddRole(context, "SuperAdmin");
        var service = CreateService(context);
        var created = await service.CreateIdentityProviderAsync(NewProvider(safe.Id, autoCreate: false), GlobalAdmin);

        await Assert.ThrowsAsync<IdentityProviderValidationException>(() =>
            service.UpdateIdentityProviderAsync(created.Id, NewProvider(privileged.Id), GlobalAdmin));

        var stored = await context.IdentityProviders.AsNoTracking().SingleAsync();
        Assert.Equal(safe.Id, stored.DefaultRoleId);
        Assert.False(stored.AutoCreateUsers);
    }

    [Fact]
    public async Task WriteMethods_ThrowAccessDenied_ForActorWithoutAuthority()
    {
        using var context = CreateContext();
        var service = CreateService(context);
        var existing = await service.CreateIdentityProviderAsync(NewProvider(null), GlobalAdmin);

        await Assert.ThrowsAsync<IdentityProviderAccessDeniedException>(() =>
            service.CreateIdentityProviderAsync(NewProvider(null), PlainUser));
        await Assert.ThrowsAsync<IdentityProviderAccessDeniedException>(() =>
            service.UpdateIdentityProviderAsync(existing.Id, NewProvider(null), PlainUser));
        await Assert.ThrowsAsync<IdentityProviderAccessDeniedException>(() =>
            service.DeleteIdentityProviderAsync(existing.Id, PlainUser));

        Assert.Single(context.IdentityProviders);
    }

    [Fact]
    public async Task Create_ThrowsValidation_ForUnknownTenant()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<IdentityProviderValidationException>(() =>
            service.CreateIdentityProviderAsync(NewProvider(null, tenantId: Guid.NewGuid()), GlobalAdmin));
    }

    // ----- login-time guard for rows that predate the rule ------------------------------------

    private static async Task<(AuthResult result, IAMDbContext context)> LoginAsNewUserAsync(Role? defaultRole)
    {
        var context = CreateContext();
        context.Tenants.Add(new Tenant { Id = TenantId, Name = "Tenant", IsActive = true });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(context, new FakeGoogleHandler("google-sub-1", "fresh.user@example.com"), cache);

        // Written straight to the DB, the way a row from before the rule (or an out-of-band edit) looks.
        var provider = NewProvider(defaultRole?.Id, TenantId);
        if (defaultRole != null)
        {
            context.Roles.Add(defaultRole);
        }
        context.IdentityProviders.Add(provider);
        await context.SaveChangesAsync();

        await service.GetAuthorizationUrlAsync(provider.Id, "https://app.example.com/callback", "state-4697");
        var result = await service.HandleCallbackAsync(provider.Id, "code", "state-4697");
        return (result, context);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    [InlineData("Admin")]
    public async Task Callback_AutoCreate_DoesNotAssignAPrivilegedDefaultRole_FromAnExistingRow(string roleName)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = roleName, Permissions = "[\"*\"]" };

        var (result, context) = await LoginAsNewUserAsync(role);
        using var _ = context;

        Assert.True(result.Success, result.Error);
        var user = await context.Users.SingleAsync(u => u.Email == "fresh.user@example.com");
        Assert.Empty(await context.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync());
        Assert.DoesNotContain(result.User!.UserRoles, ur => ur.Role.Name == roleName);
    }

    [Fact]
    public async Task Callback_AutoCreate_StillAssignsAnOrdinaryDefaultRole()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Resident", Permissions = "[\"Room.View\"]" };

        var (result, context) = await LoginAsNewUserAsync(role);
        using var _ = context;

        Assert.True(result.Success, result.Error);
        var user = await context.Users.SingleAsync(u => u.Email == "fresh.user@example.com");
        var assigned = Assert.Single(await context.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync());
        Assert.Equal(role.Id, assigned.RoleId);
        Assert.Equal(TenantId, assigned.TenantId);
    }

    [Fact]
    public async Task Callback_AutoCreate_WithoutDefaultRole_StillCreatesTheUser()
    {
        var (result, context) = await LoginAsNewUserAsync(null);
        using var _ = context;

        Assert.True(result.Success, result.Error);
        Assert.Empty(await context.UserRoles.ToListAsync());
    }
}

// Test doubles

file class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>Answers Google's token exchange and userinfo calls with a fixed profile.</summary>
file class FakeGoogleHandler : HttpMessageHandler
{
    private readonly string _sub;
    private readonly string _email;

    public FakeGoogleHandler(string sub, string email)
    {
        _sub = sub;
        _email = email;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = request.Method == HttpMethod.Post
            ? "{\"access_token\":\"fake-access-token\",\"token_type\":\"Bearer\"}"
            : $"{{\"sub\":\"{_sub}\",\"email\":\"{_email}\",\"name\":\"Fresh User\",\"given_name\":\"Fresh\",\"family_name\":\"User\"}}";

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}

file class FakeSecretsVaultService : ISecretsVaultService
{
    public Task<SecretEntry> CreateSecretAsync(
        string name, string plainTextValue, Guid? tenantId = null,
        string secretType = "Generic", string? description = null,
        string? rotationScheduleJson = null, string? tags = null,
        Guid? createdByUserId = null, CancellationToken ct = default)
        => Task.FromResult(new SecretEntry { Id = Guid.NewGuid(), Name = name });

    public Task<SecretEntry?> GetSecretAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<SecretEntry?>(null);

    public Task<string?> GetSecretValueAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<List<SecretEntry>> GetSecretsAsync(
        Guid? tenantId = null, string? secretType = null,
        bool? isActive = null, CancellationToken ct = default)
        => Task.FromResult(new List<SecretEntry>());

    public Task<SecretEntry?> UpdateSecretAsync(
        Guid id, string? name = null, string? description = null,
        string? rotationScheduleJson = null, string? tags = null,
        string? secretType = null, bool? isActive = null, CancellationToken ct = default)
        => Task.FromResult<SecretEntry?>(null);

    public Task<SecretEntry> RotateSecretAsync(
        Guid id, string newPlainTextValue, string rotationReason = "Manual",
        Guid? rotatedByUserId = null, TimeSpan? gracePeriod = null, CancellationToken ct = default)
        => Task.FromResult(new SecretEntry { Id = id });

    public Task<List<SecretVersion>> GetSecretHistoryAsync(Guid secretId, CancellationToken ct = default)
        => Task.FromResult(new List<SecretVersion>());

    public Task<bool> DeleteSecretAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<List<SecretEntry>> GetSecretsDueForRotationAsync(CancellationToken ct = default)
        => Task.FromResult(new List<SecretEntry>());
}
