using System.Reflection;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4698: the directory-sync loop itself (not just the controller) is confined to the config's tenant: no match
/// on a global email, no phone overwrite or re-enable for other tenants' users, disable-missing only for this
/// tenant's LDAP-managed users, platform-wide roles never granted, bind password stored encrypted, ldaps only.
/// </summary>
public class DirectorySyncTenantScopeTests
{
    private const string Ldaps = "ldaps://" + FakeHostResolver.PublicHost + ":636";

    private sealed class Rig
    {
        public required IAMDbContext Db { get; init; }
        public required FakeLdapDirectoryClient Ldap { get; init; }
        public required DirectorySyncService Service { get; init; }
    }

    private static Rig CreateRig(string[]? allowedHosts = null)
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new IAMDbContext(options);

        var values = new Dictionary<string, string?>
        {
            ["SecretsVault:MasterKey"] = Convert.ToBase64String(new byte[32])
        };
        for (var i = 0; i < (allowedHosts?.Length ?? 0); i++)
            values[$"DirectorySync:AllowedHosts:{i}"] = allowedHosts![i];
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var ldap = new FakeLdapDirectoryClient();
        var vault = new SecretsVaultService(db, config, NullLogger<SecretsVaultService>.Instance);
        var service = new DirectorySyncService(db, NullLogger<DirectorySyncService>.Instance, ldap, vault, new FakeHostResolver(), config);
        return new Rig { Db = db, Ldap = ldap, Service = service };
    }

    private static Tenant AddTenant(IAMDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant;
    }

    private static Role AddRole(IAMDbContext db, string name, Guid? tenantId = null)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, Permissions = "[\"Room.View\"]" };
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static User AddUser(IAMDbContext db, string email, string passwordHash = "LDAP_MANAGED", bool active = true, string? phone = null)
    {
        var user = new User { Id = Guid.NewGuid(), Email = email, PasswordHash = passwordHash, IsActive = active, PhoneNumber = phone, FirstName = "Old", LastName = "Name" };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static void Member(IAMDbContext db, User user, Role role, Guid? tenantId)
    {
        db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = role.Id, TenantId = tenantId, GrantedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    private static DirectorySyncConfig AddConfig(IAMDbContext db, Tenant tenant, string url = Ldaps,
        string bindPassword = "", Dictionary<string, Guid>? mapping = null)
    {
        var config = new DirectorySyncConfig
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Corp", LdapUrl = url, BindDn = "cn=bind",
            BindPassword = bindPassword, SearchBase = "dc=example,dc=test", SearchFilter = "(objectClass=person)",
            AttributeMapping = "{}", GroupToRoleMapping = JsonSerializer.Serialize(mapping ?? new Dictionary<string, Guid>()),
            IsActive = true
        };
        db.DirectorySyncConfigs.Add(config);
        db.SaveChanges();
        return config;
    }

    private static async Task<User> ReloadAsync(IAMDbContext db, Guid id)
    {
        db.ChangeTracker.Clear();
        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == id);
    }

    private static List<string> LogErrors(DirectorySyncLog log) =>
        log.Errors == null ? new() : JsonSerializer.Deserialize<List<string>>(log.Errors)!;

    // ----- other tenants' users ---------------------------------------------------------------

    [Fact]
    public async Task Sync_DoesNotTouchARegularUserOfAnotherTenant_WithTheSameEmail()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var b = AddTenant(rig.Db);
        var member = AddRole(rig.Db, "Resident");
        var victim = AddUser(rig.Db, "victim@corp.test", passwordHash: "bcrypt-hash", phone: "+31600000001");
        Member(rig.Db, victim, member, b.Id);
        var config = AddConfig(rig.Db, a, mapping: new() { ["cn=staff"] = member.Id });
        rig.Ldap.Entries = new() { FakeLdapDirectoryClient.Person("victim@corp.test", phone: "+31699999999", memberOf: "cn=staff") };

        var log = await rig.Service.RunFullSyncAsync(config.Id);

        var after = await ReloadAsync(rig.Db, victim.Id);
        Assert.Equal("+31600000001", after.PhoneNumber);
        Assert.Equal("Old", after.FirstName);
        Assert.Empty(rig.Db.UserRoles.Where(ur => ur.UserId == victim.Id && ur.TenantId == a.Id));
        Assert.Contains(LogErrors(log), e => e.Contains("victim@corp.test") && e.Contains("outside this tenant"));
    }

    [Fact]
    public async Task Sync_DoesNotReEnableOrChangeAnLdapManagedUserOfAnotherTenant()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var b = AddTenant(rig.Db);
        var member = AddRole(rig.Db, "Resident");
        var other = AddUser(rig.Db, "other@corp.test", active: false, phone: "+31600000002");
        Member(rig.Db, other, member, b.Id);
        var config = AddConfig(rig.Db, a);
        rig.Ldap.Entries = new() { FakeLdapDirectoryClient.Person("other@corp.test", phone: "+31688888888") };

        await rig.Service.RunFullSyncAsync(config.Id);

        var after = await ReloadAsync(rig.Db, other.Id);
        Assert.False(after.IsActive);
        Assert.Equal("+31600000002", after.PhoneNumber);
    }

    [Fact]
    public async Task Sync_DoesNotTouchAUserWithAGlobalRole()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var superAdmin = AddRole(rig.Db, "SuperAdmin");
        var admin = AddUser(rig.Db, "root@corp.test", phone: "+31600000003");
        Member(rig.Db, admin, superAdmin, null);
        var config = AddConfig(rig.Db, a);
        rig.Ldap.Entries = new() { FakeLdapDirectoryClient.Person("root@corp.test", phone: "+31677777777") };

        await rig.Service.RunFullSyncAsync(config.Id);

        Assert.Equal("+31600000003", (await ReloadAsync(rig.Db, admin.Id)).PhoneNumber);
    }

    // ----- this tenant's users -----------------------------------------------------------------

    [Fact]
    public async Task Sync_CreatesNewUsers_AndUpdatesAndReEnablesItsOwnLdapManagedUsers()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var member = AddRole(rig.Db, "Resident");
        var mine = AddUser(rig.Db, "mine@corp.test", active: false, phone: "+31600000004");
        Member(rig.Db, mine, member, a.Id);
        var config = AddConfig(rig.Db, a, mapping: new() { ["cn=staff"] = member.Id });
        rig.Ldap.Entries = new()
        {
            FakeLdapDirectoryClient.Person("mine@corp.test", phone: "+31655555555", memberOf: "cn=staff"),
            FakeLdapDirectoryClient.Person("new@corp.test", memberOf: "cn=staff"),
        };

        var log = await rig.Service.RunFullSyncAsync(config.Id);

        Assert.Equal(DirectorySyncStatus.Success, log.Status);
        Assert.Equal(1, log.UsersCreated);
        Assert.Equal(1, log.UsersUpdated);
        var after = await ReloadAsync(rig.Db, mine.Id);
        Assert.True(after.IsActive);
        Assert.Equal("+31655555555", after.PhoneNumber);
        var created = await rig.Db.Users.AsNoTracking().FirstAsync(u => u.Email == "new@corp.test");
        Assert.True(await rig.Db.UserRoles.AnyAsync(ur => ur.UserId == created.Id && ur.TenantId == a.Id && ur.RoleId == member.Id));
    }

    [Fact]
    public async Task Sync_KeepsTheOwnPhoneOfARegularMember_ButStillMapsRoles()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var resident = AddRole(rig.Db, "Resident");
        var contractor = AddRole(rig.Db, "Contractor");
        var regular = AddUser(rig.Db, "regular@corp.test", passwordHash: "bcrypt-hash", phone: "+31600000005");
        Member(rig.Db, regular, resident, a.Id);
        var config = AddConfig(rig.Db, a, mapping: new() { ["cn=contractors"] = contractor.Id });
        rig.Ldap.Entries = new() { FakeLdapDirectoryClient.Person("regular@corp.test", phone: "+31644444444", memberOf: "cn=contractors") };

        await rig.Service.RunFullSyncAsync(config.Id);

        Assert.Equal("+31600000005", (await ReloadAsync(rig.Db, regular.Id)).PhoneNumber);
        Assert.True(await rig.Db.UserRoles.AnyAsync(ur => ur.UserId == regular.Id && ur.TenantId == a.Id && ur.RoleId == contractor.Id));
    }

    // ----- disable missing ---------------------------------------------------------------------

    [Fact]
    public async Task DisableMissing_OnlyDisablesThisTenantsLdapManagedUsers()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var b = AddTenant(rig.Db);
        var member = AddRole(rig.Db, "Resident");
        var goneA = AddUser(rig.Db, "gone-a@corp.test"); Member(rig.Db, goneA, member, a.Id);
        var goneB = AddUser(rig.Db, "gone-b@corp.test"); Member(rig.Db, goneB, member, b.Id);
        var both = AddUser(rig.Db, "both@corp.test"); Member(rig.Db, both, member, a.Id); Member(rig.Db, both, member, b.Id);
        var regularA = AddUser(rig.Db, "regular-a@corp.test", passwordHash: "bcrypt-hash"); Member(rig.Db, regularA, member, a.Id);
        var config = AddConfig(rig.Db, a);
        rig.Ldap.Entries = new() { FakeLdapDirectoryClient.Person("someone@corp.test") };

        var log = await rig.Service.RunFullSyncAsync(config.Id);

        Assert.Equal(1, log.UsersDisabled);
        Assert.False((await ReloadAsync(rig.Db, goneA.Id)).IsActive);
        Assert.True((await ReloadAsync(rig.Db, goneB.Id)).IsActive);
        Assert.True((await ReloadAsync(rig.Db, both.Id)).IsActive);
        Assert.True((await ReloadAsync(rig.Db, regularA.Id)).IsActive);
    }

    [Fact]
    public async Task DeltaSync_NeverDisablesAnyone()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var member = AddRole(rig.Db, "Resident");
        var gone = AddUser(rig.Db, "gone@corp.test"); Member(rig.Db, gone, member, a.Id);
        var config = AddConfig(rig.Db, a);
        config.LastSyncAt = DateTime.UtcNow.AddHours(-1);
        rig.Db.SaveChanges();
        rig.Ldap.Entries = new() { FakeLdapDirectoryClient.Person("someone@corp.test") };

        var log = await rig.Service.RunDeltaSyncAsync(config.Id);

        Assert.Equal(0, log.UsersDisabled);
        Assert.True((await ReloadAsync(rig.Db, gone.Id)).IsActive);
        Assert.Contains("whenChanged", rig.Ldap.Calls.Single().Filter);
    }

    // ----- group-to-role mapping ---------------------------------------------------------------

    [Fact]
    public async Task Sync_IgnoresStoredMappingsToPlatformForeignAndUnknownRoles()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var other = AddTenant(rig.Db);
        var superAdmin = AddRole(rig.Db, "SuperAdmin");
        var fakeSystemAdmin = AddRole(rig.Db, "SystemAdmin", tenantId: a.Id);
        var foreign = AddRole(rig.Db, "Front Desk", tenantId: other.Id);
        var resident = AddRole(rig.Db, "Resident");
        var config = AddConfig(rig.Db, a, mapping: new()
        {
            ["cn=root"] = superAdmin.Id,
            ["cn=sysadmin"] = fakeSystemAdmin.Id,
            ["cn=foreign"] = foreign.Id,
            ["cn=ghost"] = Guid.NewGuid(),
            ["cn=staff"] = resident.Id
        });
        rig.Ldap.Entries = new() { FakeLdapDirectoryClient.Person("climber@corp.test", memberOf: new[] { "cn=root", "cn=sysadmin", "cn=foreign", "cn=ghost", "cn=staff" }) };

        var log = await rig.Service.RunFullSyncAsync(config.Id);

        var user = await rig.Db.Users.AsNoTracking().FirstAsync(u => u.Email == "climber@corp.test");
        var granted = await rig.Db.UserRoles.Where(ur => ur.UserId == user.Id).Select(ur => ur.RoleId).ToListAsync();
        Assert.Equal(new[] { resident.Id }, granted);
        Assert.Equal(DirectorySyncStatus.PartialSuccess, log.Status);
        Assert.Equal(4, LogErrors(log).Count(e => e.Contains("ignored")));
        Assert.Equal(1, log.GroupsSynced);
    }

    // ----- bind password -----------------------------------------------------------------------

    [Fact]
    public async Task CreateConfig_StoresTheBindPasswordEncrypted_AndSyncUsesThePlainValue()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var config = new DirectorySyncConfig
        {
            TenantId = a.Id, Name = "Corp", LdapUrl = Ldaps, BindDn = "cn=bind", BindPassword = "Pl41n-Secret!",
            SearchBase = "dc=x", AttributeMapping = "{}", GroupToRoleMapping = "{}"
        };

        var created = await rig.Service.CreateConfigAsync(config);

        Assert.StartsWith("enc:", created.BindPassword);
        Assert.DoesNotContain("Pl41n-Secret!", created.BindPassword);
        rig.Db.ChangeTracker.Clear();
        var stored = await rig.Db.DirectorySyncConfigs.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        Assert.DoesNotContain("Pl41n-Secret!", stored.BindPassword);
        Assert.All(rig.Db.SecretEntries.AsNoTracking().ToList(), s => Assert.DoesNotContain("Pl41n-Secret!", s.EncryptedValue));

        await rig.Service.RunFullSyncAsync(created.Id);
        Assert.Equal("Pl41n-Secret!", rig.Ldap.Calls.Single().BindPassword);
    }

    [Fact]
    public async Task UpdateConfig_WithoutAPassword_KeepsTheStoredOne_WithOneItReplacesIt()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var created = await rig.Service.CreateConfigAsync(new DirectorySyncConfig
        {
            TenantId = a.Id, Name = "Corp", LdapUrl = Ldaps, BindPassword = "first-pw", SearchBase = "dc=x",
            AttributeMapping = "{}", GroupToRoleMapping = "{}"
        });
        var firstRef = created.BindPassword;

        await rig.Service.UpdateConfigAsync(new DirectorySyncConfig { Id = created.Id, Name = "Corp 2", LdapUrl = Ldaps, SearchBase = "dc=x", AttributeMapping = "{}", GroupToRoleMapping = "{}" });
        rig.Db.ChangeTracker.Clear();
        Assert.Equal(firstRef, (await rig.Db.DirectorySyncConfigs.AsNoTracking().FirstAsync(c => c.Id == created.Id)).BindPassword);

        await rig.Service.UpdateConfigAsync(new DirectorySyncConfig { Id = created.Id, Name = "Corp 2", LdapUrl = Ldaps, BindPassword = "second-pw", SearchBase = "dc=x", AttributeMapping = "{}", GroupToRoleMapping = "{}" });
        rig.Db.ChangeTracker.Clear();
        var updated = await rig.Db.DirectorySyncConfigs.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        Assert.StartsWith("enc:", updated.BindPassword);
        Assert.NotEqual(firstRef, updated.BindPassword);

        await rig.Service.RunFullSyncAsync(created.Id);
        Assert.Equal("second-pw", rig.Ldap.Calls.Single().BindPassword);
    }

    [Fact]
    public async Task LegacyPlaintextPasswords_AreEncryptedBySweep_AndAreIdempotent()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var legacy = AddConfig(rig.Db, a, bindPassword: "legacy-plain-pw");
        var empty = AddConfig(rig.Db, a, bindPassword: "");

        Assert.Equal(1, await rig.Service.EncryptLegacyBindPasswordsAsync());
        Assert.Equal(0, await rig.Service.EncryptLegacyBindPasswordsAsync());

        rig.Db.ChangeTracker.Clear();
        Assert.StartsWith("enc:", (await rig.Db.DirectorySyncConfigs.AsNoTracking().FirstAsync(c => c.Id == legacy.Id)).BindPassword);
        Assert.Equal("", (await rig.Db.DirectorySyncConfigs.AsNoTracking().FirstAsync(c => c.Id == empty.Id)).BindPassword);

        await rig.Service.RunFullSyncAsync(legacy.Id);
        Assert.Equal("legacy-plain-pw", rig.Ldap.Calls.Single().BindPassword);
    }

    [Fact]
    public async Task LegacyPlaintextPassword_IsEncryptedOnFirstSyncToo()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var legacy = AddConfig(rig.Db, a, bindPassword: "legacy-plain-pw");

        await rig.Service.RunFullSyncAsync(legacy.Id);

        rig.Db.ChangeTracker.Clear();
        Assert.StartsWith("enc:", (await rig.Db.DirectorySyncConfigs.AsNoTracking().FirstAsync(c => c.Id == legacy.Id)).BindPassword);
        Assert.Equal("legacy-plain-pw", rig.Ldap.Calls.Single().BindPassword);
    }

    // ----- LDAP URL ----------------------------------------------------------------------------

    [Theory]
    [InlineData("ldap://ldap.public-directory.test:389")]
    [InlineData("http://ldap.public-directory.test")]
    [InlineData("not a url")]
    [InlineData("ldaps://user:pw@ldap.public-directory.test")]
    public async Task CreateConfig_RefusesNonLdapsOrMalformedUrls_EvenForSuperAdmin(string url)
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);

        var ex = await Assert.ThrowsAsync<DirectorySyncValidationException>(() => rig.Service.CreateConfigAsync(
            new DirectorySyncConfig { TenantId = a.Id, Name = "x", LdapUrl = url, AttributeMapping = "{}", GroupToRoleMapping = "{}" },
            callerIsSuperAdmin: true));

        Assert.Contains("LDAP URL", ex.Message);
    }

    [Theory]
    [InlineData("ldaps://" + FakeHostResolver.PrivateHost)]
    [InlineData("ldaps://" + FakeHostResolver.MixedHost)]
    [InlineData("ldaps://127.0.0.1")]
    [InlineData("ldaps://10.1.2.3:636")]
    [InlineData("ldaps://169.254.169.254")]
    [InlineData("ldaps://[::1]")]
    [InlineData("ldaps://unresolvable.directory.test")]
    public async Task CreateConfig_RefusesPrivateAddresses_OutsideSuperAdmin(string url)
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var config = () => new DirectorySyncConfig { TenantId = a.Id, Name = "x", LdapUrl = url, AttributeMapping = "{}", GroupToRoleMapping = "{}" };

        await Assert.ThrowsAsync<DirectorySyncValidationException>(() => rig.Service.CreateConfigAsync(config()));
    }

    [Fact]
    public async Task CreateConfig_SuperAdminMayPointAtAnInternalDirectory()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);

        var created = await rig.Service.CreateConfigAsync(
            new DirectorySyncConfig { TenantId = a.Id, Name = "x", LdapUrl = "ldaps://" + FakeHostResolver.PrivateHost, AttributeMapping = "{}", GroupToRoleMapping = "{}" },
            callerIsSuperAdmin: true);

        Assert.NotEqual(Guid.Empty, created.Id);
    }

    [Fact]
    public async Task Sync_RefusesAStoredPlainLdapOrPrivateUrl_AndNeverCallsTheServer()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var plain = AddConfig(rig.Db, a, url: "ldap://" + FakeHostResolver.PublicHost);
        var internalHost = AddConfig(rig.Db, a, url: "ldaps://" + FakeHostResolver.PrivateHost);

        var plainLog = await rig.Service.RunFullSyncAsync(plain.Id);
        var internalLog = await rig.Service.RunFullSyncAsync(internalHost.Id);
        var test = await rig.Service.TestConnectionAsync(internalHost.Id);

        Assert.Equal(DirectorySyncStatus.Failed, plainLog.Status);
        Assert.Equal(DirectorySyncStatus.Failed, internalLog.Status);
        Assert.False(test.Success);
        Assert.Empty(rig.Ldap.Calls);
    }

    [Fact]
    public async Task Sync_AllowsAnInternalHostOnTheDeployAllowlist()
    {
        var rig = CreateRig(allowedHosts: new[] { FakeHostResolver.PrivateHost });
        var a = AddTenant(rig.Db);
        var config = AddConfig(rig.Db, a, url: "ldaps://" + FakeHostResolver.PrivateHost);

        var log = await rig.Service.RunFullSyncAsync(config.Id);

        Assert.Equal(DirectorySyncStatus.Success, log.Status);
        Assert.Single(rig.Ldap.Calls);
    }

    // ----- validation of the mapping at save time ----------------------------------------------

    [Fact]
    public async Task CreateConfig_RejectsMappingsToPlatformUnknownAndForeignRoles()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var other = AddTenant(rig.Db);
        var superAdmin = AddRole(rig.Db, "SuperAdmin");
        var systemAdmin = AddRole(rig.Db, "SystemAdmin");
        var fake = AddRole(rig.Db, "securityadmin", tenantId: a.Id);
        var foreign = AddRole(rig.Db, "Front Desk", tenantId: other.Id);
        var ok = AddRole(rig.Db, "Front Desk", tenantId: a.Id);

        foreach (var bad in new[] { superAdmin.Id, systemAdmin.Id, fake.Id, foreign.Id, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<DirectorySyncValidationException>(() => rig.Service.CreateConfigAsync(
                new DirectorySyncConfig
                {
                    TenantId = a.Id, Name = "x", LdapUrl = Ldaps, AttributeMapping = "{}",
                    GroupToRoleMapping = JsonSerializer.Serialize(new Dictionary<string, Guid> { ["cn=g"] = bad })
                }, callerIsSuperAdmin: true));
        }

        var created = await rig.Service.CreateConfigAsync(new DirectorySyncConfig
        {
            TenantId = a.Id, Name = "x", LdapUrl = Ldaps, AttributeMapping = "{}",
            GroupToRoleMapping = JsonSerializer.Serialize(new Dictionary<string, Guid> { ["cn=g"] = ok.Id })
        });
        Assert.NotEqual(Guid.Empty, created.Id);
    }

    [Fact]
    public void RoleMappingRules_RefuseEveryRoleTheApiAuthorizesOn_ExceptTheTenantRoles()
    {
        // A role name that unlocks an [Authorize(Roles = ...)] endpoint unlocks it platform-wide (claims carry the
        // name only). When someone adds a new role to an attribute this fails until it is classified in
        // DirectoryRoleMappingRules (platform-wide -> PlatformRoleNames, tenant role -> GrantableTenantRoleNames).
        var authorizedRoles = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => new MemberInfo[] { t }.Concat(t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            .SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>(inherit: false))
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
            .SelectMany(a => a.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(authorizedRoles);
        var unclassified = authorizedRoles
            .Where(r => !DirectoryRoleMappingRules.PlatformRoleNames.Contains(r)
                        && !DirectoryRoleMappingRules.GrantableTenantRoleNames.Contains(r))
            .ToList();
        Assert.True(unclassified.Count == 0,
            "Roles used in [Authorize(Roles)] but not classified for directory sync: " + string.Join(", ", unclassified));

        var tenant = Guid.NewGuid();
        foreach (var name in authorizedRoles.Except(DirectoryRoleMappingRules.GrantableTenantRoleNames, StringComparer.OrdinalIgnoreCase))
        {
            Assert.Equal(DirectoryRoleProblem.PlatformRole,
                DirectoryRoleMappingRules.Check(new Role { Name = name, TenantId = tenant }, tenant));
        }
    }

    [Fact]
    public async Task CreateConfig_RejectsAMappingToACustomTenantAdminRole()
    {
        var rig = CreateRig();
        var a = AddTenant(rig.Db);
        var tenantAdmin = AddRole(rig.Db, "TenantAdmin", tenantId: a.Id);

        await Assert.ThrowsAsync<DirectorySyncValidationException>(() => rig.Service.CreateConfigAsync(
            new DirectorySyncConfig
            {
                TenantId = a.Id, Name = "x", LdapUrl = Ldaps, AttributeMapping = "{}",
                GroupToRoleMapping = JsonSerializer.Serialize(new Dictionary<string, Guid> { ["cn=g"] = tenantAdmin.Id })
            }, callerIsSuperAdmin: true));
    }

    [Fact]
    public void RoleMappingRules_ClassifyByNameNotByTenantOwnership()
    {
        var tenant = Guid.NewGuid();

        Assert.Equal(DirectoryRoleProblem.NotFound, DirectoryRoleMappingRules.Check(null, tenant));
        Assert.Equal(DirectoryRoleProblem.PlatformRole, DirectoryRoleMappingRules.Check(new Role { Name = " superadmin " }, tenant));
        Assert.Equal(DirectoryRoleProblem.PlatformRole, DirectoryRoleMappingRules.Check(new Role { Name = "Admin", TenantId = tenant }, tenant));
        Assert.Equal(DirectoryRoleProblem.OtherTenantRole, DirectoryRoleMappingRules.Check(new Role { Name = "Member", TenantId = Guid.NewGuid() }, tenant));
        Assert.Equal(DirectoryRoleProblem.None, DirectoryRoleMappingRules.Check(new Role { Name = "BuildingOwner" }, tenant));
        Assert.Equal(DirectoryRoleProblem.None, DirectoryRoleMappingRules.Check(new Role { Name = "Member", TenantId = tenant }, tenant));
    }
}
