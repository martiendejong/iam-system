using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 3162: the tenant policy for legacy (SHA-1) authenticator enrollments lives on
/// /api/organization-settings/{tenantId} (admins of the tenant only, like every other setting there), and
/// /api/mfa/status tells a member whether their own enrollment is a legacy one.
/// </summary>
public class LegacyTotpEndpointTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid TenantAdminRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01");

    private readonly IAMTestWebApplicationFactory _factory;

    public LegacyTotpEndpointTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ----- helpers ----------------------------------------------------------------------------

    private async Task<Tenant> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<User> CreateUserAsync(
        Guid? tenantId, TwoFactorMethod method = TwoFactorMethod.None, string? algorithm = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@legacy-totp-endpoints.test",
            PasswordHash = "not-used",
            TwoFactorEnabled = method != TwoFactorMethod.None,
            TwoFactorMethod = method,
            TwoFactorSecret = method == TwoFactorMethod.Totp ? "JBSWY3DPEHPK3PXP" : null,
            TotpAlgorithm = algorithm
        };
        db.Users.Add(user);
        if (tenantId.HasValue)
        {
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(), UserId = user.Id, RoleId = TenantAdminRoleId, TenantId = tenantId, GrantedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<OrganizationSettings?> SettingsFromDbAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.OrganizationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId);
    }

    private HttpClient ClientAs(Guid userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@legacy-totp-endpoints.test", roles, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient SuperAdminClient() => ClientAs(Guid.NewGuid(), "SuperAdmin");

    private static string Url(Guid tenantId) => $"/api/organization-settings/{tenantId}";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    // ----- tenant policy: read ----------------------------------------------------------------

    [Fact]
    public async Task Get_TenantWithoutSettings_ReportsTheEmailPinDefault()
    {
        var tenant = await CreateTenantAsync();

        var response = await SuperAdminClient().GetAsync(Url(tenant.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("EmailPin", body.GetProperty("legacyTotpMigration").GetString());
        Assert.Equal(0, body.GetProperty("legacyTotpUserCount").GetInt32());
    }

    [Fact]
    public async Task Get_CountsOnlyThisTenantsLegacyAuthenticatorMembers()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();

        await CreateUserAsync(tenant.Id, TwoFactorMethod.Totp, algorithm: null);                        // legacy: counted
        await CreateUserAsync(tenant.Id, TwoFactorMethod.Totp, algorithm: "SHA1");                       // legacy: counted
        await CreateUserAsync(tenant.Id, TwoFactorMethod.Totp, algorithm: TotpAlgorithms.Sha256);        // current: no
        await CreateUserAsync(tenant.Id, TwoFactorMethod.Email);                                         // e-mail PIN: no
        await CreateUserAsync(tenant.Id);                                                                // no 2FA: no
        await CreateUserAsync(otherTenant.Id, TwoFactorMethod.Totp, algorithm: null);                    // other tenant: no

        var body = await ReadAsync(await SuperAdminClient().GetAsync(Url(tenant.Id)));

        Assert.Equal(2, body.GetProperty("legacyTotpUserCount").GetInt32());
    }

    // ----- tenant policy: write ---------------------------------------------------------------

    [Theory]
    [InlineData("Off", LegacyTotpMigrationMode.Off)]
    [InlineData("off", LegacyTotpMigrationMode.Off)]
    [InlineData("EmailPin", LegacyTotpMigrationMode.EmailPin)]
    [InlineData("emailpin", LegacyTotpMigrationMode.EmailPin)]
    public async Task Put_StoresTheChosenPolicy_AndGetReadsItBack(string requested, LegacyTotpMigrationMode expected)
    {
        var tenant = await CreateTenantAsync();
        var client = SuperAdminClient();

        var put = await client.PutAsJsonAsync(Url(tenant.Id), new { legacyTotpMigration = requested });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(expected.ToString(), (await ReadAsync(put)).GetProperty("legacyTotpMigration").GetString());
        Assert.Equal(expected, (await SettingsFromDbAsync(tenant.Id))!.LegacyTotpMigration);

        var get = await ReadAsync(await client.GetAsync(Url(tenant.Id)));
        Assert.Equal(expected.ToString(), get.GetProperty("legacyTotpMigration").GetString());
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task Put_UnknownPolicy_IsRejectedAndNothingChanges(string requested)
    {
        var tenant = await CreateTenantAsync();
        var client = SuperAdminClient();
        (await client.PutAsJsonAsync(Url(tenant.Id), new { legacyTotpMigration = "Off", welcomeMessage = "kept" })).EnsureSuccessStatusCode();

        var put = await client.PutAsJsonAsync(Url(tenant.Id), new { legacyTotpMigration = requested, welcomeMessage = "changed" });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var stored = (await SettingsFromDbAsync(tenant.Id))!;
        Assert.Equal(LegacyTotpMigrationMode.Off, stored.LegacyTotpMigration);
        Assert.Equal("kept", stored.WelcomeMessage);
    }

    [Fact]
    public async Task Put_WithoutThePolicyField_LeavesItAlone()
    {
        var tenant = await CreateTenantAsync();
        var client = SuperAdminClient();
        (await client.PutAsJsonAsync(Url(tenant.Id), new { legacyTotpMigration = "Off" })).EnsureSuccessStatusCode();

        var put = await client.PutAsJsonAsync(Url(tenant.Id), new { welcomeMessage = "hello" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(LegacyTotpMigrationMode.Off, (await SettingsFromDbAsync(tenant.Id))!.LegacyTotpMigration);
    }

    [Fact]
    public async Task Put_AMemberWhoIsNotAdminOfTheTenant_CannotChangeThePolicy()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var plainUser = await CreateUserAsync(tenantId: null);
        var adminOfOtherTenant = await CreateUserAsync(otherTenant.Id);

        var asPlainUser = await ClientAs(plainUser.Id).PutAsJsonAsync(Url(tenant.Id), new { legacyTotpMigration = "Off" });
        var asOtherAdmin = await ClientAs(adminOfOtherTenant.Id, "TenantAdmin").PutAsJsonAsync(Url(tenant.Id), new { legacyTotpMigration = "Off" });

        Assert.Equal(HttpStatusCode.Forbidden, asPlainUser.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asOtherAdmin.StatusCode);
        Assert.Null(await SettingsFromDbAsync(tenant.Id));
    }

    [Fact]
    public async Task Put_TheTenantsOwnAdmin_CanChangeThePolicy()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateUserAsync(tenant.Id);

        var put = await ClientAs(admin.Id, "TenantAdmin").PutAsJsonAsync(Url(tenant.Id), new { legacyTotpMigration = "Off" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(LegacyTotpMigrationMode.Off, (await SettingsFromDbAsync(tenant.Id))!.LegacyTotpMigration);
    }

    // ----- /api/mfa/status --------------------------------------------------------------------

    [Theory]
    [InlineData(TwoFactorMethod.Totp, null, true)]
    [InlineData(TwoFactorMethod.Totp, "SHA256", false)]
    [InlineData(TwoFactorMethod.Email, null, false)]
    [InlineData(TwoFactorMethod.None, null, false)]
    public async Task MfaStatus_FlagsALegacyAuthenticatorEnrollment(TwoFactorMethod method, string? algorithm, bool expected)
    {
        var user = await CreateUserAsync(tenantId: null, method, algorithm);

        var response = await ClientAs(user.Id).GetAsync("/api/mfa/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, (await ReadAsync(response)).GetProperty("legacyTotpEnrollment").GetBoolean());
    }

    // ----- portal security summary ------------------------------------------------------------

    [Theory]
    [InlineData(TwoFactorMethod.Totp, "SHA256", "totp")]
    [InlineData(TwoFactorMethod.Email, null, "email")]
    public async Task PortalSecuritySummary_ReportsTheRealMfaMethod(TwoFactorMethod method, string? algorithm, string expected)
    {
        // A member migrated from a legacy authenticator app is on e-mail PIN; the summary must not call that "totp".
        var user = await CreateUserAsync(tenantId: null, method, algorithm);

        var body = await ReadAsync(await ClientAs(user.Id).GetAsync("/api/portal/security-summary"));

        Assert.True(body.GetProperty("mfaEnabled").GetBoolean());
        Assert.Equal(expected, body.GetProperty("mfaMethod").GetString());
    }

    [Fact]
    public async Task PortalSecuritySummary_WithoutTwoFactor_HasNoMethod()
    {
        var user = await CreateUserAsync(tenantId: null);

        var body = await ReadAsync(await ClientAs(user.Id).GetAsync("/api/portal/security-summary"));

        Assert.False(body.GetProperty("mfaEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("mfaMethod").ValueKind);
    }
}
