using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5148: the certificate authority API issues device identities (with the private key), renews and revokes
/// them. Issue, renew, revoke, info and the expiring list need SuperAdmin or a building owner/manager of the
/// device's own tenant (the tenant comes from the stored device/certificate); a manager of another tenant gets the
/// same 404 as for an unknown id; members, outsiders, device and service-account tokens are refused. Validity and key
/// size are capped and the name fields cannot add name parts to the subject.
/// </summary>
public sealed class CertificateAuthorityAuthorizationTests : IClassFixture<CertificateAuthorityAuthorizationTests.CaFactory>, IDisposable
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string CaDirectory = Path.Combine(Path.GetTempPath(), "ca5148-" + Guid.NewGuid().ToString("N"));

    /// <summary>Keeps the CA key file of these tests in a temp folder instead of the machine-wide default.</summary>
    public sealed class CaFactory : IAMTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ca:CertificatePath", Path.Combine(CaDirectory, "ca.pfx"));
            base.ConfigureWebHost(builder);
        }
    }

    private readonly CaFactory _factory;

    public CertificateAuthorityAuthorizationTests(CaFactory factory) => _factory = factory;

    public void Dispose()
    {
        // The factory is shared by the class, so the folder is left for the OS temp cleanup rather than deleted
        // while another test may still hold the CA file open.
    }

    // ----- helpers ---------------------------------------------------------------------------

    private async Task<Guid> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@ca-authz.test", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task GrantRoleAsync(Guid userId, string roleName, Guid? tenantId, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role == null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName, TenantId = RootTenantId };
            db.Roles.Add(role);
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id,
            TenantId = tenantId,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid Id, string DeviceId)> SeedDeviceAsync(Guid tenantId, bool isActive = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceId = $"dev-{Guid.NewGuid():N}",
            Name = "seeded",
            DeviceType = "sensor",
            AuthenticationMethod = "certificate",
            TenantId = tenantId,
            ResourcePath = "acme:hq:floor-1:sensor:1",
            Permissions = "[]",
            IsActive = isActive
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return (device.Id, device.DeviceId);
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@ca-authz.test", roles, tenantClaim));

    /// <summary>A user holding the given role in the given tenant (a null tenant = an unscoped, "global" row).</summary>
    private async Task<HttpClient> UserWithRoleAsync(string roleName, Guid? tenantId, DateTime? expiresAt = null)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, roleName, tenantId, expiresAt);
        return ClientAs(userId, new[] { roleName });
    }

    private async Task<HttpClient> SuperAdminAsync() => ClientAs(await CreateUserAsync(), new[] { "SuperAdmin" });

    private static object IssueBody(Guid deviceId, string commonName = "device-1", string? organization = null,
        string? organizationalUnit = null, int? validityDays = null, int? keySizeBits = null) => new
    {
        deviceId,
        commonName,
        organization,
        organizationalUnit,
        validityDays,
        keySizeBits
    };

    /// <summary>Issues a certificate as SuperAdmin and returns its id.</summary>
    private async Task<Guid> IssueAsSuperAdminAsync(Guid deviceId)
    {
        var response = await (await SuperAdminAsync()).PostAsJsonAsync("/api/ca/issue", IssueBody(deviceId));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"seed issue failed: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("certificateId").GetGuid();
    }

    private async Task<DeviceCertificate?> CertificateRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.DeviceCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    private async Task<int> CertificateCountAsync(Guid deviceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.DeviceCertificates.CountAsync(c => c.DeviceId == deviceId);
    }

    private static readonly object RevokeBody = new { reason = "test" };

    // ----- issue -----------------------------------------------------------------------------

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingOwner")]
    [InlineData("BuildingManager")]
    public async Task Issue_ManagerOfTheDevicesTenant_Succeeds_AndGetsThePrivateKey(string role)
    {
        var tenant = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(tenant);
        var client = await UserWithRoleAsync(role, tenant);

        var response = await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("PRIVATE KEY", json.GetProperty("privateKeyPem").GetString());
        Assert.Equal(1, await CertificateCountAsync(id));
    }

    [Fact]
    public async Task Issue_SuperAdmin_Succeeds_ForAnyTenant()
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());

        var response = await (await SuperAdminAsync()).PostAsJsonAsync("/api/ca/issue", IssueBody(id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Issue_ManagerOfAnotherTenant_GetsTheSame404AsForAnUnknownDevice_AndNothingIsIssued()
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());
        var client = await UserWithRoleAsync("BuildingManager", await CreateTenantAsync());

        var foreign = await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id));
        var unknown = await client.PostAsJsonAsync("/api/ca/issue", IssueBody(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());
        Assert.Equal(0, await CertificateCountAsync(id));
    }

    [Fact]
    public async Task Issue_Member_Gets403_AndNothingIsIssued()
    {
        var tenant = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(tenant);
        var client = await UserWithRoleAsync("User", tenant);

        var response = await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CertificateCountAsync(id));
    }

    [Fact]
    public async Task Issue_Outsider_Gets403_ForARealAndAnUnknownDeviceAlike()
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());
        var client = ClientAs(await CreateUserAsync(), new[] { "User" });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/ca/issue", IssueBody(Guid.NewGuid()))).StatusCode);
        Assert.Equal(0, await CertificateCountAsync(id));
    }

    [Fact]
    public async Task Issue_ManagerWhoseRoleExpired_Gets403()
    {
        var tenant = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(tenant);
        var client = await UserWithRoleAsync("BuildingManager", tenant, DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id))).StatusCode);
    }

    [Fact]
    public async Task Issue_Anonymous_Gets401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/ca/issue", IssueBody(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceAndServiceAccountTokens_AreRefusedOnEveryProtectedAction()
    {
        var tenant = await CreateTenantAsync();
        var (id, deviceId) = await SeedDeviceAsync(tenant);
        var certificateId = await IssueAsSuperAdminAsync(id);
        var clients = new[]
        {
            ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(id, deviceId, tenant)),
            ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "devices:read", "devices:write"))
        };

        foreach (var client in clients)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/ca/renew/{certificateId}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/ca/revoke/{certificateId}", RevokeBody)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/ca/info")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/ca/expiring?days=3650")).StatusCode);
        }

        Assert.Equal("Active", (await CertificateRowAsync(certificateId))!.Status);
        Assert.Equal(1, await CertificateCountAsync(id));
    }

    // ----- renew -----------------------------------------------------------------------------

    [Fact]
    public async Task Renew_ManagerOfTheDevicesTenant_AndSuperAdmin_Succeed()
    {
        var tenant = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(tenant);
        var first = await IssueAsSuperAdminAsync(id);

        var asManager = await (await UserWithRoleAsync("BuildingOwner", tenant)).PostAsync($"/api/ca/renew/{first}", null);
        Assert.Equal(HttpStatusCode.OK, asManager.StatusCode);
        var second = (await asManager.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("certificateId").GetGuid();

        var asSuperAdmin = await (await SuperAdminAsync()).PostAsync($"/api/ca/renew/{second}", null);
        Assert.Equal(HttpStatusCode.OK, asSuperAdmin.StatusCode);
        Assert.Equal("Revoked", (await CertificateRowAsync(first))!.Status);
    }

    [Fact]
    public async Task Renew_ManagerOfAnotherTenant_Gets404_Member_And_Outsider_Get403_AndNothingChanges()
    {
        var tenant = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(tenant);
        var certificateId = await IssueAsSuperAdminAsync(id);

        var otherManager = await UserWithRoleAsync("TenantAdmin", await CreateTenantAsync());
        var member = await UserWithRoleAsync("User", tenant);
        var outsider = ClientAs(await CreateUserAsync(), new[] { "User" });

        var foreign = await otherManager.PostAsync($"/api/ca/renew/{certificateId}", null);
        var unknown = await otherManager.PostAsync($"/api/ca/renew/{Guid.NewGuid()}", null);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/ca/renew/{certificateId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync($"/api/ca/renew/{certificateId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync($"/api/ca/renew/{Guid.NewGuid()}", null)).StatusCode);

        Assert.Equal("Active", (await CertificateRowAsync(certificateId))!.Status);
        Assert.Equal(1, await CertificateCountAsync(id));
    }

    // ----- revoke ----------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_ManagerOfTheDevicesTenant_AndSuperAdmin_Succeed()
    {
        var tenant = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(tenant);
        var byManager = await IssueAsSuperAdminAsync(id);
        var bySuperAdmin = await IssueAsSuperAdminAsync(id);

        var managerResponse = await (await UserWithRoleAsync("BuildingManager", tenant))
            .PostAsJsonAsync($"/api/ca/revoke/{byManager}", RevokeBody);
        var superAdminResponse = await (await SuperAdminAsync())
            .PostAsJsonAsync($"/api/ca/revoke/{bySuperAdmin}", RevokeBody);

        Assert.Equal(HttpStatusCode.OK, managerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, superAdminResponse.StatusCode);
        Assert.Equal("Revoked", (await CertificateRowAsync(byManager))!.Status);
        Assert.Equal("Revoked", (await CertificateRowAsync(bySuperAdmin))!.Status);
    }

    [Fact]
    public async Task Revoke_ManagerOfAnotherTenant_Gets404_Member_And_Outsider_Get403_AndTheCertificateStaysActive()
    {
        var tenant = await CreateTenantAsync();
        var (id, _) = await SeedDeviceAsync(tenant);
        var certificateId = await IssueAsSuperAdminAsync(id);

        var otherManager = await UserWithRoleAsync("BuildingOwner", await CreateTenantAsync());
        var member = await UserWithRoleAsync("User", tenant);
        var outsider = ClientAs(await CreateUserAsync(), new[] { "User" });

        Assert.Equal(HttpStatusCode.NotFound, (await otherManager.PostAsJsonAsync($"/api/ca/revoke/{certificateId}", RevokeBody)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherManager.PostAsJsonAsync($"/api/ca/revoke/{Guid.NewGuid()}", RevokeBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/ca/revoke/{certificateId}", RevokeBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/ca/revoke/{certificateId}", RevokeBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/ca/revoke/{Guid.NewGuid()}", RevokeBody)).StatusCode);

        Assert.Equal("Active", (await CertificateRowAsync(certificateId))!.Status);
    }

    // ----- info, public certificate, crl, expiring -------------------------------------------

    [Fact]
    public async Task Info_IsForSuperAdminAndManagers_Only()
    {
        var tenant = await CreateTenantAsync();

        Assert.Equal(HttpStatusCode.OK, (await (await SuperAdminAsync()).GetAsync("/api/ca/info")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await UserWithRoleAsync("BuildingManager", tenant)).GetAsync("/api/ca/info")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await UserWithRoleAsync("User", tenant)).GetAsync("/api/ca/info")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(await CreateUserAsync(), new[] { "User" }).GetAsync("/api/ca/info")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/ca/info")).StatusCode);
    }

    [Fact]
    public async Task PublicCaCertificate_StaysReadable_ForAnySignedInUser_AndTheCrlForAnyone()
    {
        var outsider = ClientAs(await CreateUserAsync(), new[] { "User" });

        var certificate = await outsider.GetAsync("/api/ca/certificate");
        var crl = await _factory.CreateClient().GetAsync("/api/ca/crl");

        Assert.Equal(HttpStatusCode.OK, certificate.StatusCode);
        Assert.Contains("BEGIN CERTIFICATE", await certificate.Content.ReadAsStringAsync());
        Assert.DoesNotContain("PRIVATE KEY", await certificate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, crl.StatusCode);
    }

    [Fact]
    public async Task Expiring_ShowsEachManagerOnlyTheirOwnTenants_SuperAdminAll_MembersAreRefused()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var (deviceA, _) = await SeedDeviceAsync(tenantA);
        var (deviceB, _) = await SeedDeviceAsync(tenantB);
        var certA = await IssueAsSuperAdminAsync(deviceA);
        var certB = await IssueAsSuperAdminAsync(deviceB);

        static async Task<List<Guid>> IdsAsync(HttpResponseMessage r)
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var json = await r.Content.ReadFromJsonAsync<JsonElement>();
            return json.EnumerateArray().Select(e => e.GetProperty("certificateId").GetGuid()).ToList();
        }

        var asManagerA = await IdsAsync(await (await UserWithRoleAsync("BuildingManager", tenantA)).GetAsync("/api/ca/expiring?days=400"));
        var asManagerB = await IdsAsync(await (await UserWithRoleAsync("TenantAdmin", tenantB)).GetAsync("/api/ca/expiring?days=400"));
        var asSuperAdmin = await IdsAsync(await (await SuperAdminAsync()).GetAsync("/api/ca/expiring?days=400"));

        Assert.Contains(certA, asManagerA);
        Assert.DoesNotContain(certB, asManagerA);
        Assert.Contains(certB, asManagerB);
        Assert.DoesNotContain(certA, asManagerB);
        Assert.Contains(certA, asSuperAdmin);
        Assert.Contains(certB, asSuperAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await UserWithRoleAsync("User", tenantA)).GetAsync("/api/ca/expiring?days=400")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(await CreateUserAsync(), new[] { "User" }).GetAsync("/api/ca/expiring?days=400")).StatusCode);
    }

    // ----- validity, key size, names ---------------------------------------------------------

    [Fact]
    public async Task Issue_DefaultsTo365Days_And2048Bits()
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());

        var response = await (await SuperAdminAsync()).PostAsJsonAsync("/api/ca/issue", IssueBody(id));

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var days = (json.GetProperty("notAfter").GetDateTime() - json.GetProperty("notBefore").GetDateTime()).TotalDays;
        Assert.InRange(days, 364.9, 366.1);
        using var cert = X509CertificateLoader.LoadCertificate(Encoding.UTF8.GetBytes(json.GetProperty("certificatePem").GetString()!));
        Assert.Equal(2048, cert.GetRSAPublicKey()!.KeySize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(826)]
    [InlineData(36500)]
    public async Task Issue_ValidityOutsideTheCap_Gets400_AndNothingIsIssued(int validityDays)
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());

        var response = await (await SuperAdminAsync()).PostAsJsonAsync("/api/ca/issue", IssueBody(id, validityDays: validityDays));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CertificateCountAsync(id));
    }

    [Fact]
    public async Task Issue_TheMaximumValidityOf825Days_IsAccepted()
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());

        var response = await (await SuperAdminAsync()).PostAsJsonAsync("/api/ca/issue", IssueBody(id, validityDays: 825));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2049)]
    [InlineData(8192)]
    [InlineData(16384)]
    public async Task Issue_KeySizeOutsideTheAllowList_Gets400_AndNothingIsIssued(int keySizeBits)
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());

        var response = await (await SuperAdminAsync()).PostAsJsonAsync("/api/ca/issue", IssueBody(id, keySizeBits: keySizeBits));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CertificateCountAsync(id));
    }

    [Fact]
    public async Task Issue_AKeySizeOnTheAllowList_IsAccepted()
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());

        var response = await (await SuperAdminAsync()).PostAsJsonAsync("/api/ca/issue", IssueBody(id, keySizeBits: 3072));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var cert = X509CertificateLoader.LoadCertificate(Encoding.UTF8.GetBytes(json.GetProperty("certificatePem").GetString()!));
        Assert.Equal(3072, cert.GetRSAPublicKey()!.KeySize);
    }

    [Fact]
    public async Task Issue_SeparatorsInTheNameFields_DoNotAddNameParts()
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());

        var response = await (await SuperAdminAsync()).PostAsJsonAsync("/api/ca/issue", IssueBody(
            id,
            commonName: "dev-1, OU=x",
            organization: "Acme, O=Evil+OU=y",
            organizationalUnit: "Ops\",CN=admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var cert = X509CertificateLoader.LoadCertificate(Encoding.UTF8.GetBytes(json.GetProperty("certificatePem").GetString()!));
        var parts = cert.SubjectName.EnumerateRelativeDistinguishedNames().ToList();

        // Exactly the three name parts that were asked for, each holding its text literally.
        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.False(p.HasMultipleElements));
        string ValueOf(string oid) => parts.Single(p => p.GetSingleElementType().Value == oid).GetSingleElementValue()!;
        Assert.Equal("dev-1, OU=x", ValueOf("2.5.4.3"));
        Assert.Equal("Acme, O=Evil+OU=y", ValueOf("2.5.4.10"));
        Assert.Equal("Ops\",CN=admin", ValueOf("2.5.4.11"));
    }

    [Fact]
    public async Task Issue_RenewKeepsTheEscapedNameAsOnePart()
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());
        var superAdmin = await SuperAdminAsync();
        var issued = await superAdmin.PostAsJsonAsync("/api/ca/issue", IssueBody(id, commonName: "dev-1, OU=x"));
        var first = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("certificateId").GetGuid();

        var renewed = await superAdmin.PostAsync($"/api/ca/renew/{first}", null);

        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var json = await renewed.Content.ReadFromJsonAsync<JsonElement>();
        using var cert = X509CertificateLoader.LoadCertificate(Encoding.UTF8.GetBytes(json.GetProperty("certificatePem").GetString()!));
        var part = Assert.Single(cert.SubjectName.EnumerateRelativeDistinguishedNames());
        Assert.Equal("dev-1, OU=x", part.GetSingleElementValue());
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("bad\rname")]
    [InlineData("bad\0name")]
    public async Task Issue_ControlCharactersInAName_Get400(string name)
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());
        var client = await SuperAdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id, commonName: name))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id, organization: name))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id, organizationalUnit: name))).StatusCode);
        Assert.Equal(0, await CertificateCountAsync(id));
    }

    [Fact]
    public async Task Issue_MissingOrOverlongCommonName_Gets400()
    {
        var (id, _) = await SeedDeviceAsync(await CreateTenantAsync());
        var client = await SuperAdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id, commonName: " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/ca/issue", IssueBody(id, commonName: new string('a', 65)))).StatusCode);
        Assert.Equal(0, await CertificateCountAsync(id));
    }
}
