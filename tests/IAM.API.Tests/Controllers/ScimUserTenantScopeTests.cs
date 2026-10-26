using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4699: a SCIM token belongs to one tenant, and the user endpoints (list, get, replace, patch, delete) only see
/// and change users of that tenant - a non-expired UserRoles row for it, or a successful Create in its SCIM log.
/// Everything else is a 404; users who also hold a tenant-less or other-tenant role are never changed; an email or
/// phone number cannot be moved onto another account (409 without naming it).
/// </summary>
public class ScimUserTenantScopeTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private const string Domain = "@scim4699.test";

    private readonly IAMTestWebApplicationFactory _factory;

    public ScimUserTenantScopeTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ----- world -------------------------------------------------------------------------------

    private sealed record World(
        Tenant A, Tenant B, string TokenA, string TokenB,
        User MemberA, User MultiTenant, User AdminInA, User MemberB, User GlobalSuperAdmin, User ExpiredInA, User PreRoleUser);

    private IAMDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAMDbContext>();

    private async Task<World> CreateWorldAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = Db(scope);
        var scim = scope.ServiceProvider.GetRequiredService<IScimService>();

        var a = new Tenant { Id = Guid.NewGuid(), Name = $"A {Guid.NewGuid():N}", IsActive = true };
        var b = new Tenant { Id = Guid.NewGuid(), Name = $"B {Guid.NewGuid():N}", IsActive = true };
        var resident = new Role { Id = Guid.NewGuid(), Name = "Resident", Permissions = "[]" };
        var superAdminRole = new Role { Id = Guid.NewGuid(), Name = "SuperAdmin", Permissions = "[\"*\"]" };
        db.Tenants.AddRange(a, b);
        db.Roles.AddRange(resident, superAdminRole);

        User NewUser(string label, string? phone = null) => new()
        {
            Id = Guid.NewGuid(), Email = $"{label}-{Guid.NewGuid():N}{Domain}", FirstName = label, LastName = "Person",
            PasswordHash = "x", IsActive = true, PhoneNumber = phone
        };
        var memberA = NewUser("membera", "+31611110001");
        var multi = NewUser("multi", "+31611110002");
        var adminInA = NewUser("admina", "+31611110003");
        var memberB = NewUser("memberb", "+31622220001");
        var global = NewUser("globaladmin", "+31622220002");
        var expired = NewUser("expired", "+31611110004");
        var preRole = NewUser("prerole", "+31611110005");
        db.Users.AddRange(memberA, multi, adminInA, memberB, global, expired, preRole);

        UserRole Row(User u, Role r, Guid? tenant, DateTime? exp = null) =>
            new() { Id = Guid.NewGuid(), UserId = u.Id, RoleId = r.Id, TenantId = tenant, ExpiresAt = exp, GrantedAt = DateTime.UtcNow };
        db.UserRoles.AddRange(
            Row(memberA, resident, a.Id),
            Row(multi, resident, a.Id), Row(multi, resident, b.Id),
            Row(adminInA, resident, a.Id), Row(adminInA, superAdminRole, null),
            Row(memberB, resident, b.Id),
            Row(global, superAdminRole, null),
            Row(expired, resident, a.Id, DateTime.UtcNow.AddMinutes(-5)));
        // A user provisioned through A's SCIM before the fix: only the create log row ties them to A.
        db.ScimProvisioningLogs.Add(new ScimProvisioningLog
        {
            TenantId = a.Id, Operation = "Create", ResourceType = "User", ResourceId = preRole.Id, Status = "Success"
        });
        await db.SaveChangesAsync();

        var tokenA = (await scim.CreateTokenAsync(a.Id, "A", null, null)).plainTextValue;
        var tokenB = (await scim.CreateTokenAsync(b.Id, "B", null, null)).plainTextValue;
        return new World(a, b, tokenA, tokenB, memberA, multi, adminInA, memberB, global, expired, preRole);
    }

    private HttpClient ScimClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<User> UserFromDbAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await Db(scope).Users.AsNoTracking().FirstAsync(u => u.Id == id);
    }

    private static object UserBody(string userName, string? given = "New", string? phone = null, bool active = true) => new
    {
        schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
        userName,
        name = new { givenName = given, familyName = "Name" },
        emails = new[] { new { value = userName, primary = true } },
        phoneNumbers = phone == null ? null : new[] { new { value = phone } },
        active
    };

    private static object PatchBody(string path, object value) => new
    {
        schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
        Operations = new[] { new { op = "replace", path, value } }
    };

    private static JsonElement Prop(JsonElement json, string name) =>
        json.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static List<Guid> IdsOf(JsonElement list) =>
        Prop(list, "Resources").EnumerateArray().Select(r => Guid.Parse(Prop(r, "id").GetString()!)).ToList();

    private static async Task<List<Guid>> ListIdsAsync(HttpResponseMessage response) =>
        IdsOf(await response.Content.ReadFromJsonAsync<JsonElement>());

    // ----- get ---------------------------------------------------------------------------------

    [Fact]
    public async Task Get_OwnTenantUsers_AreVisible_IncludingTheLogOnlyUser()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenA);

        foreach (var user in new[] { w.MemberA, w.MultiTenant, w.AdminInA, w.PreRoleUser })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/scim/v2/Users/{user.Id}")).StatusCode);
    }

    [Fact]
    public async Task Get_OtherTenantUsers_SuperAdmins_ExpiredMembers_AndUnknownIds_AreNotFound()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenA);

        foreach (var id in new[] { w.MemberB.Id, w.GlobalSuperAdmin.Id, w.ExpiredInA.Id, Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/scim/v2/Users/{id}")).StatusCode);
    }

    [Fact]
    public async Task Get_TheOtherTenantsToken_CannotSeeTheFirstTenantsUsers()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenB);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/scim/v2/Users/{w.MemberA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/scim/v2/Users/{w.PreRoleUser.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/scim/v2/Users/{w.MemberB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/scim/v2/Users/{w.MultiTenant.Id}")).StatusCode);
    }

    [Fact]
    public async Task Users_WithoutAToken_AreUnauthorized()
    {
        var w = await CreateWorldAsync();
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/scim/v2/Users/{w.MemberA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/scim/v2/Users")).StatusCode);
    }

    // ----- replace / patch / delete: other tenants and platform users --------------------------

    [Fact]
    public async Task Replace_Patch_Delete_OnAnotherTenantsUser_Return404_AndChangeNothing()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenA);
        var victim = w.MemberB;
        var original = await UserFromDbAsync(victim.Id);

        var put = await client.PutAsJsonAsync($"/scim/v2/Users/{victim.Id}", UserBody($"takeover-{Guid.NewGuid():N}{Domain}", phone: "+31699990000", active: false));
        var patchEmail = await client.PatchAsJsonAsync($"/scim/v2/Users/{victim.Id}", PatchBody("userName", $"takeover2-{Guid.NewGuid():N}{Domain}"));
        var patchActive = await client.PatchAsJsonAsync($"/scim/v2/Users/{victim.Id}", PatchBody("active", false));
        var delete = await client.DeleteAsync($"/scim/v2/Users/{victim.Id}");

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patchEmail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patchActive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        var after = await UserFromDbAsync(victim.Id);
        Assert.Equal(original.Email, after.Email);
        Assert.Equal(original.PhoneNumber, after.PhoneNumber);
        Assert.Equal(original.FirstName, after.FirstName);
        Assert.True(after.IsActive);
    }

    [Fact]
    public async Task Replace_Patch_Delete_OnASuperAdminOfAnotherTenant_Return404()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenA);
        var victim = w.GlobalSuperAdmin;
        var original = await UserFromDbAsync(victim.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/scim/v2/Users/{victim.Id}", UserBody($"x-{Guid.NewGuid():N}{Domain}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/scim/v2/Users/{victim.Id}", PatchBody("userName", $"y-{Guid.NewGuid():N}{Domain}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/scim/v2/Users/{victim.Id}")).StatusCode);
        Assert.Equal(original.Email, (await UserFromDbAsync(victim.Id)).Email);
        Assert.True((await UserFromDbAsync(victim.Id)).IsActive);
    }

    [Fact]
    public async Task Replace_Patch_Delete_OnAnInTenantUserWhoAlsoHoldsAnotherTenantOrGlobalRole_Return404()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenA);

        foreach (var victim in new[] { w.MultiTenant, w.AdminInA })
        {
            var original = await UserFromDbAsync(victim.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/scim/v2/Users/{victim.Id}", UserBody($"z-{Guid.NewGuid():N}{Domain}"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/scim/v2/Users/{victim.Id}", PatchBody("active", false))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/scim/v2/Users/{victim.Id}")).StatusCode);
            var after = await UserFromDbAsync(victim.Id);
            Assert.Equal(original.Email, after.Email);
            Assert.True(after.IsActive);
        }
    }

    [Fact]
    public async Task Replace_Patch_Delete_OnAnExpiredMember_Return404()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenA);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/scim/v2/Users/{w.ExpiredInA.Id}", UserBody($"e-{Guid.NewGuid():N}{Domain}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/scim/v2/Users/{w.ExpiredInA.Id}")).StatusCode);
    }

    // ----- replace / patch / delete: own tenant ------------------------------------------------

    [Fact]
    public async Task Replace_Patch_Delete_OwnTenantUsers_Work_IncludingTheLogOnlyUser()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenA);
        var newEmail = $"renamed-{Guid.NewGuid():N}{Domain}";

        var put = await client.PutAsJsonAsync($"/scim/v2/Users/{w.MemberA.Id}", UserBody(newEmail, given: "Renamed"));
        var patch = await client.PatchAsJsonAsync($"/scim/v2/Users/{w.PreRoleUser.Id}", PatchBody("active", false));
        var delete = await client.DeleteAsync($"/scim/v2/Users/{w.PreRoleUser.Id}");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var renamed = await UserFromDbAsync(w.MemberA.Id);
        Assert.Equal(newEmail, renamed.Email);
        Assert.Equal("Renamed", renamed.FirstName);
        Assert.False((await UserFromDbAsync(w.PreRoleUser.Id)).IsActive);
    }

    [Fact]
    public async Task Delete_UnknownUser_Is404()
    {
        var w = await CreateWorldAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await ScimClient(w.TokenA).DeleteAsync($"/scim/v2/Users/{Guid.NewGuid()}")).StatusCode);
    }

    // ----- list and filters --------------------------------------------------------------------

    [Fact]
    public async Task List_ShowsOnlyTheTokensTenant_AndCountsExcludeEverythingElse()
    {
        var w = await CreateWorldAsync();

        var response = await ScimClient(w.TokenA).GetAsync($"/scim/v2/Users?count=1000&filter=userName co \"{Domain}\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = IdsOf(json);
        Assert.Equal(new[] { w.MemberA.Id, w.MultiTenant.Id, w.AdminInA.Id, w.PreRoleUser.Id }.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.Equal(4, Prop(json, "totalResults").GetInt32());
        Assert.DoesNotContain(w.MemberB.Id, ids);
        Assert.DoesNotContain(w.GlobalSuperAdmin.Id, ids);
        Assert.DoesNotContain(w.ExpiredInA.Id, ids);
    }

    [Fact]
    public async Task List_WithoutAFilter_NeverLeaksOtherTenants()
    {
        var w = await CreateWorldAsync();

        var idsA = await ListIdsAsync(await ScimClient(w.TokenA).GetAsync("/scim/v2/Users?count=1000"));
        var idsB = await ListIdsAsync(await ScimClient(w.TokenB).GetAsync("/scim/v2/Users?count=1000"));

        Assert.DoesNotContain(w.MemberB.Id, idsA);
        Assert.DoesNotContain(w.GlobalSuperAdmin.Id, idsA);
        Assert.DoesNotContain(w.MemberA.Id, idsB);
        Assert.DoesNotContain(w.PreRoleUser.Id, idsB);
        Assert.Contains(w.MemberB.Id, idsB);
    }

    [Fact]
    public async Task Filters_CannotWidenTheTenantScope()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenA);
        var victimEmail = (await UserFromDbAsync(w.MemberB.Id)).Email;
        var superEmail = (await UserFromDbAsync(w.GlobalSuperAdmin.Id)).Email;

        var filters = new[]
        {
            $"userName eq \"{victimEmail}\"",
            $"emails.value eq \"{victimEmail}\"",
            $"userName eq \"{superEmail}\"",
            $"id eq {w.MemberB.Id}",
            $"id eq {w.GlobalSuperAdmin.Id}",
            $"userName sw \"memberb\"",
            $"name.givenName eq \"memberb\"",
            $"userName co \"{Domain}\" and active eq true",
            "externalId eq \"anything\"",
        };

        foreach (var filter in filters)
        {
            var ids = await ListIdsAsync(await client.GetAsync($"/scim/v2/Users?count=1000&filter={Uri.EscapeDataString(filter)}"));
            Assert.DoesNotContain(w.MemberB.Id, ids);
            Assert.DoesNotContain(w.GlobalSuperAdmin.Id, ids);
            Assert.DoesNotContain(w.ExpiredInA.Id, ids);
        }

        var own = await ListIdsAsync(await client.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString($"id eq {w.MemberA.Id}")}"));
        Assert.Equal(new[] { w.MemberA.Id }, own);
    }

    [Fact]
    public async Task List_SortingAndPaging_StayInsideTheTenant()
    {
        var w = await CreateWorldAsync();
        var client = ScimClient(w.TokenA);

        var page = await client.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString($"userName co \"{Domain}\"")}&sortBy=userName&sortOrder=descending&startIndex=2&count=2");

        var json = await page.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, Prop(json, "totalResults").GetInt32());
        Assert.Equal(2, Prop(json, "Resources").GetArrayLength());
        var ids = IdsOf(json);
        Assert.DoesNotContain(w.MemberB.Id, ids);
    }

    // ----- create keeps the user in scope ------------------------------------------------------

    [Fact]
    public async Task Create_RecordsTheUserId_SoTheUserStaysInScope_AndIsInvisibleToOtherTenants()
    {
        var w = await CreateWorldAsync();
        var email = $"fresh-{Guid.NewGuid():N}{Domain}";

        var create = await ScimClient(w.TokenA).PostAsJsonAsync("/scim/v2/Users", UserBody(email));

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = Guid.Parse(Prop(await create.Content.ReadFromJsonAsync<JsonElement>(), "id").GetString()!);
        using (var scope = _factory.Services.CreateScope())
        {
            Assert.True(await Db(scope).ScimProvisioningLogs.AnyAsync(l =>
                l.TenantId == w.A.Id && l.Operation == "Create" && l.ResourceType == "User" && l.ResourceId == id && l.Status == "Success"));
        }
        Assert.Equal(HttpStatusCode.OK, (await ScimClient(w.TokenA).GetAsync($"/scim/v2/Users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ScimClient(w.TokenB).GetAsync($"/scim/v2/Users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ScimClient(w.TokenA).PatchAsJsonAsync($"/scim/v2/Users/{id}", PatchBody("active", false))).StatusCode);
    }

    [Fact]
    public async Task Create_WithAnEmailAlreadyOnThePlatform_Is409()
    {
        var w = await CreateWorldAsync();
        var taken = (await UserFromDbAsync(w.MemberB.Id)).Email;

        var response = await ScimClient(w.TokenA).PostAsJsonAsync("/scim/v2/Users", UserBody(taken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ----- email / phone collisions ------------------------------------------------------------

    [Fact]
    public async Task Replace_ToAnEmailTakenByAnotherAccount_Is409_WithoutRevealingWhoseIt_AndChangesNothing()
    {
        var w = await CreateWorldAsync();
        var victim = await UserFromDbAsync(w.MemberB.Id);
        var before = await UserFromDbAsync(w.MemberA.Id);

        var response = await ScimClient(w.TokenA).PutAsJsonAsync($"/scim/v2/Users/{w.MemberA.Id}", UserBody(victim.Email));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(victim.Email, body);
        Assert.DoesNotContain(victim.Id.ToString(), body);
        Assert.DoesNotContain(victim.FirstName, body);
        Assert.Equal(before.Email, (await UserFromDbAsync(w.MemberA.Id)).Email);
        Assert.Equal(victim.Email, (await UserFromDbAsync(w.MemberB.Id)).Email);
    }

    [Fact]
    public async Task Replace_ToAnEmailDifferingOnlyInCase_Is409()
    {
        var w = await CreateWorldAsync();
        var victim = await UserFromDbAsync(w.MemberB.Id);

        var response = await ScimClient(w.TokenA).PutAsJsonAsync($"/scim/v2/Users/{w.MemberA.Id}", UserBody(victim.Email.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Patch_ToAnEmailTakenByAnotherAccount_Is409_AndTheUserIsUnchangedAfterwards()
    {
        var w = await CreateWorldAsync();
        var victim = await UserFromDbAsync(w.MemberB.Id);
        var before = await UserFromDbAsync(w.MemberA.Id);
        var client = ScimClient(w.TokenA);

        var userName = await client.PatchAsJsonAsync($"/scim/v2/Users/{w.MemberA.Id}", PatchBody("userName", victim.Email));
        var emails = await client.PatchAsJsonAsync($"/scim/v2/Users/{w.MemberA.Id}", PatchBody("emails", victim.Email));

        Assert.Equal(HttpStatusCode.Conflict, userName.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, emails.StatusCode);
        Assert.Equal(before.Email, (await UserFromDbAsync(w.MemberA.Id)).Email);
    }

    [Fact]
    public async Task Replace_ToAPhoneNumberOfAnotherAccount_Is409()
    {
        var w = await CreateWorldAsync();
        var victim = await UserFromDbAsync(w.MemberB.Id);
        var own = await UserFromDbAsync(w.MemberA.Id);

        var response = await ScimClient(w.TokenA).PutAsJsonAsync($"/scim/v2/Users/{w.MemberA.Id}", UserBody(own.Email, phone: victim.PhoneNumber));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(own.PhoneNumber, (await UserFromDbAsync(w.MemberA.Id)).PhoneNumber);
    }

    [Fact]
    public async Task Replace_KeepingTheSameEmailAndPhone_IsNotACollision()
    {
        var w = await CreateWorldAsync();
        var own = await UserFromDbAsync(w.MemberA.Id);

        var response = await ScimClient(w.TokenA).PutAsJsonAsync($"/scim/v2/Users/{w.MemberA.Id}", UserBody(own.Email, given: "Same", phone: own.PhoneNumber));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Same", (await UserFromDbAsync(w.MemberA.Id)).FirstName);
    }
}
