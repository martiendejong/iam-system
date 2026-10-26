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
/// Task 5151: every /api/visitors action is checked against the tenant of the visitor. Managers (TenantAdmin,
/// BuildingOwner, BuildingManager role row in the tenant, or SuperAdmin) do everything for their tenant's visitors; a
/// plain tenant member registers visitors hosted by themselves and sees, checks in, checks out and gets the QR of the
/// visitors they host; everyone else gets 403 (register, list) or 404 (an id, exactly like an unknown id). Device and
/// service-account tokens get 403. API-key callers are covered by <see cref="VisitorAuthorizationApiKeyTests"/>.
/// </summary>
public class VisitorAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IAMTestWebApplicationFactory _factory;

    public VisitorAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

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
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@visitor-authz.test",
            FirstName = "Host",
            LastName = "Person",
            PasswordHash = "x"
        };
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

    private sealed record SeededVisitor(Guid Id, Guid TenantId, Guid HostUserId, string Email, string QrToken);

    private async Task<SeededVisitor> SeedVisitorAsync(
        Guid tenantId, Guid hostUserId, VisitorStatus status = VisitorStatus.PreRegistered, DateTime? visitDate = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var visitor = new Visitor
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            HostUserId = hostUserId,
            Name = $"Visitor {Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@visitor-authz-guest.test",
            Company = "Secret Company BV",
            VisitDate = visitDate ?? DateTime.UtcNow.AddDays(1),
            Status = status,
            CheckInAt = status == VisitorStatus.CheckedIn ? DateTime.UtcNow : null
        };
        db.Visitors.Add(visitor);
        db.VisitorAccessGrants.Add(new VisitorAccessGrant
        {
            VisitorId = visitor.Id,
            Resources = "[\"server-room\"]",
            ValidFrom = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddHours(8)
        });
        await db.SaveChangesAsync();
        return new SeededVisitor(visitor.Id, tenantId, hostUserId, visitor.Email, visitor.QrToken);
    }

    private async Task<Visitor?> VisitorRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Visitors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id);
    }

    private async Task<List<Visitor>> VisitorRowsByEmailAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Visitors.AsNoTracking().Include(v => v.AccessGrants).Where(v => v.Email == email).ToListAsync();
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@visitor-authz.test", roles, tenantClaim));

    /// <summary>A user holding the given role in the given tenant (a null tenant = an unscoped, "global" row).</summary>
    private async Task<(Guid UserId, HttpClient Client)> UserWithRoleAsync(
        string roleName, Guid? tenantId, DateTime? expiresAt = null, string? tenantClaim = null)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, roleName, tenantId, expiresAt);
        return (userId, ClientAs(userId, new[] { roleName }, tenantClaim));
    }

    private async Task<(Guid UserId, HttpClient Client)> SuperAdminAsync()
    {
        var userId = await CreateUserAsync();
        return (userId, ClientAs(userId, new[] { "SuperAdmin" }));
    }

    private static string NewGuestEmail() => $"{Guid.NewGuid():N}@visitor-authz-new.test";

    private static object RegisterBody(Guid tenantId, string email, Guid? hostUserId = null, object[]? grants = null) => new
    {
        tenantId,
        name = "New Visitor",
        email,
        company = "Guest BV",
        visitDate = DateTime.UtcNow.AddDays(1),
        purpose = "meeting",
        hostUserId,
        accessGrants = grants
    };

    private static object Grant(DateTime from, DateTime until) => new
    {
        resources = new[] { "floor-2" },
        validFrom = from,
        validUntil = until
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<List<Guid>> VisitorIdsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        return json.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    private static readonly string[] ActionNames = { "register", "list", "get", "check-in", "check-out", "qr" };

    /// <summary>One call per action, all aimed at the given visitor/tenant; order matches <see cref="ActionNames"/>.</summary>
    private static async Task<HttpStatusCode[]> AllActionsAsync(HttpClient c, Guid tenantId, Guid visitorId) => new[]
    {
        (await c.PostAsJsonAsync("/api/visitors", RegisterBody(tenantId, NewGuestEmail()))).StatusCode,
        (await c.GetAsync($"/api/visitors?tenantId={tenantId}")).StatusCode,
        (await c.GetAsync($"/api/visitors/{visitorId}")).StatusCode,
        (await c.PostAsync($"/api/visitors/{visitorId}/checkin", null)).StatusCode,
        (await c.PostAsync($"/api/visitors/{visitorId}/checkout", null)).StatusCode,
        (await c.GetAsync($"/api/visitors/{visitorId}/qr")).StatusCode,
    };

    private static void AssertAll(HttpStatusCode expected, HttpStatusCode[] statuses, params int[] indexes)
    {
        foreach (var i in indexes)
            Assert.True(statuses[i] == expected, $"{ActionNames[i]} returned {statuses[i]}, expected {expected}");
    }

    /// <summary>The caller must learn nothing about the visitor: no QR token, e-mail, company or grant resources.</summary>
    private static void AssertNoVisitorData(string body, SeededVisitor visitor, string what)
    {
        Assert.False(body.Contains(visitor.QrToken, StringComparison.OrdinalIgnoreCase), $"{what} leaked the QR token");
        Assert.False(body.Contains(visitor.Email, StringComparison.OrdinalIgnoreCase), $"{what} leaked the e-mail");
        Assert.False(body.Contains("Secret Company", StringComparison.OrdinalIgnoreCase), $"{what} leaked the company");
        Assert.False(body.Contains("server-room", StringComparison.OrdinalIgnoreCase), $"{what} leaked the grants");
    }

    private async Task AssertVisitorUntouchedAsync(SeededVisitor visitor)
    {
        var row = await VisitorRowAsync(visitor.Id);
        Assert.NotNull(row);
        Assert.Equal(VisitorStatus.PreRegistered, row!.Status);
        Assert.Null(row.CheckInAt);
        Assert.Null(row.CheckOutAt);
    }

    // ----- callers that are refused outright ---------------------------------------------------

    [Fact]
    public async Task Anonymous_Gets401_OnEveryAction()
    {
        var tenant = await CreateTenantAsync();
        var visitor = await SeedVisitorAsync(tenant, await CreateUserAsync());

        var statuses = await AllActionsAsync(_factory.CreateClient(), tenant, visitor.Id);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }

    [Fact]
    public async Task DeviceToken_Gets403_OnEveryAction_AndNothingChanges()
    {
        var tenant = await CreateTenantAsync();
        var host = await CreateUserAsync();
        var visitor = await SeedVisitorAsync(tenant, host);
        // A device token of the very tenant: still not a tenant member or administrator.
        var client = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(Guid.NewGuid(), "dev-1", tenant));

        var statuses = await AllActionsAsync(client, tenant, visitor.Id);

        AssertAll(HttpStatusCode.Forbidden, statuses, 0, 1, 2, 3, 4, 5);
        await AssertVisitorUntouchedAsync(visitor);
    }

    [Fact]
    public async Task DeviceToken_WhoseSubjectIsTheHost_StillGets403()
    {
        var tenant = await CreateTenantAsync();
        var hostId = Guid.NewGuid();
        var visitor = await SeedVisitorAsync(tenant, hostId);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(hostId, "dev-2", tenant));

        var statuses = await AllActionsAsync(client, tenant, visitor.Id);

        AssertAll(HttpStatusCode.Forbidden, statuses, 0, 1, 2, 3, 4, 5);
        await AssertVisitorUntouchedAsync(visitor);
    }

    [Fact]
    public async Task ServiceAccountToken_Gets403_OnEveryAction()
    {
        var tenant = await CreateTenantAsync();
        var visitor = await SeedVisitorAsync(tenant, await CreateUserAsync());
        var client = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "visitors:read", "visitors:write"));

        var statuses = await AllActionsAsync(client, tenant, visitor.Id);

        AssertAll(HttpStatusCode.Forbidden, statuses, 0, 1, 2, 3, 4, 5);
        await AssertVisitorUntouchedAsync(visitor);
    }

    // ----- outsiders: no role anywhere, or a member of another tenant ---------------------------

    [Fact]
    public async Task UserWithoutMembership_IsRefused_AndLearnsNothing()
    {
        var tenant = await CreateTenantAsync();
        var visitor = await SeedVisitorAsync(tenant, await CreateUserAsync());
        var client = ClientAs(await CreateUserAsync(), new[] { "User" });
        var email = NewGuestEmail();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, email))).StatusCode);
        Assert.Empty(await VisitorRowsByEmailAsync(email));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/visitors?tenantId={tenant}")).StatusCode);

        var unknown = await client.GetAsync($"/api/visitors/{Guid.NewGuid()}");
        foreach (var response in new[]
                 {
                     await client.GetAsync($"/api/visitors/{visitor.Id}"),
                     await client.PostAsync($"/api/visitors/{visitor.Id}/checkin", null),
                     await client.PostAsync($"/api/visitors/{visitor.Id}/checkout", null),
                     await client.GetAsync($"/api/visitors/{visitor.Id}/qr"),
                 })
        {
            // A real id of someone else's visitor looks exactly like an unknown id.
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(await unknown.Content.ReadAsStringAsync(), body);
            AssertNoVisitorData(body, visitor, "404 body");
        }
        await AssertVisitorUntouchedAsync(visitor);
    }

    [Fact]
    public async Task MemberOfAnotherTenant_IsRefused_ForEveryAction_AndNothingChanges()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var visitor = await SeedVisitorAsync(tenantA, await CreateUserAsync());
        var (_, client) = await UserWithRoleAsync("User", tenantB);

        var statuses = await AllActionsAsync(client, tenantA, visitor.Id);

        AssertAll(HttpStatusCode.Forbidden, statuses, 0, 1);
        AssertAll(HttpStatusCode.NotFound, statuses, 2, 3, 4, 5);
        await AssertVisitorUntouchedAsync(visitor);
    }

    [Fact]
    public async Task ManagerOfAnotherTenant_IsRefused_ForEveryAction_AndNothingChanges()
    {
        var own = await CreateTenantAsync();
        var victim = await CreateTenantAsync();
        var visitor = await SeedVisitorAsync(victim, await CreateUserAsync());
        var (_, client) = await UserWithRoleAsync("BuildingManager", own);
        var email = NewGuestEmail();

        // The body names the victim tenant: the manager of another tenant cannot register there, not even as host.
        var register = await client.PostAsJsonAsync("/api/visitors", RegisterBody(victim, email));
        var statuses = await AllActionsAsync(client, victim, visitor.Id);

        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
        Assert.Empty(await VisitorRowsByEmailAsync(email));
        AssertAll(HttpStatusCode.Forbidden, statuses, 0, 1);
        AssertAll(HttpStatusCode.NotFound, statuses, 2, 3, 4, 5);
        await AssertVisitorUntouchedAsync(visitor);
    }

    [Fact]
    public async Task OtherTenantsCallers_NeverReceiveTheVisitorsData()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var visitor = await SeedVisitorAsync(tenantA, await CreateUserAsync());
        var (_, member) = await UserWithRoleAsync("User", tenantB);
        var (_, manager) = await UserWithRoleAsync("TenantAdmin", tenantB);

        foreach (var client in new[] { member, manager })
        {
            foreach (var url in new[]
                     {
                         $"/api/visitors?tenantId={tenantA}",
                         $"/api/visitors/{visitor.Id}",
                         $"/api/visitors/{visitor.Id}/qr",
                         $"/api/visitors?tenantId={tenantB}",
                     })
            {
                var body = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
                AssertNoVisitorData(body, visitor, url);
            }
        }
    }

    // ----- plain member: hosts and sees their own visitors only ---------------------------------

    [Fact]
    public async Task Member_RegistersVisitorHostedByThemselves_WithOrWithoutNamingThemselves()
    {
        var tenant = await CreateTenantAsync();
        var (memberId, client) = await UserWithRoleAsync("User", tenant);
        var implicitHost = NewGuestEmail();
        var explicitHost = NewGuestEmail();

        var first = await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, implicitHost));
        var second = await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, explicitHost, hostUserId: memberId));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var rowA = Assert.Single(await VisitorRowsByEmailAsync(implicitHost));
        var rowB = Assert.Single(await VisitorRowsByEmailAsync(explicitHost));
        Assert.Equal(tenant, rowA.TenantId);
        Assert.Equal(memberId, rowA.HostUserId);
        Assert.Equal(memberId, rowB.HostUserId);
    }

    [Fact]
    public async Task Member_CannotRegisterForAnotherHost_EvenAnotherMemberOfTheTenant()
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("User", tenant);
        var (colleagueId, _) = await UserWithRoleAsync("User", tenant);
        var email = NewGuestEmail();

        var response = await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, email, hostUserId: colleagueId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await VisitorRowsByEmailAsync(email));
    }

    [Fact]
    public async Task Member_ListShowsOnlyTheVisitorsTheyHost()
    {
        var tenant = await CreateTenantAsync();
        var (memberId, client) = await UserWithRoleAsync("User", tenant);
        var (colleagueId, _) = await UserWithRoleAsync("User", tenant);
        var mine1 = await SeedVisitorAsync(tenant, memberId);
        var mine2 = await SeedVisitorAsync(tenant, memberId, VisitorStatus.CheckedIn);
        var colleagues = await SeedVisitorAsync(tenant, colleagueId);

        var all = await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}"));
        var checkedIn = await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}&status=CheckedIn"));

        Assert.Equal(new[] { mine1.Id, mine2.Id }.OrderBy(x => x), all.OrderBy(x => x));
        Assert.DoesNotContain(colleagues.Id, all);
        Assert.Equal(new[] { mine2.Id }, checkedIn);
    }

    [Fact]
    public async Task Member_CanSeeCheckInCheckOutAndGetQr_OfAVisitorTheyHost()
    {
        var tenant = await CreateTenantAsync();
        var (memberId, client) = await UserWithRoleAsync("User", tenant);
        var visitor = await SeedVisitorAsync(tenant, memberId);

        var get = await client.GetAsync($"/api/visitors/{visitor.Id}");
        var qr = await client.GetAsync($"/api/visitors/{visitor.Id}/qr");
        var checkIn = await client.PostAsync($"/api/visitors/{visitor.Id}/checkin", null);
        var checkOut = await client.PostAsync($"/api/visitors/{visitor.Id}/checkout", null);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(visitor.Email, (await JsonAsync(get)).GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal(visitor.QrToken, (await JsonAsync(qr)).GetProperty("qrToken").GetString());
        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
        Assert.Equal("CheckedIn", (await JsonAsync(checkIn)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, checkOut.StatusCode);
        Assert.Equal(VisitorStatus.CheckedOut, (await VisitorRowAsync(visitor.Id))!.Status);
    }

    [Fact]
    public async Task Member_CannotTouchAColleaguesVisitor_AndTheAnswerIsAnUnknownId404()
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("User", tenant);
        var (colleagueId, _) = await UserWithRoleAsync("User", tenant);
        var visitor = await SeedVisitorAsync(tenant, colleagueId);

        var statuses = await AllActionsAsync(client, tenant, visitor.Id);

        // register succeeds (the member hosts it), the list is allowed (but filtered), the four id actions are 404.
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Equal(HttpStatusCode.OK, statuses[1]);
        AssertAll(HttpStatusCode.NotFound, statuses, 2, 3, 4, 5);
        var body = await (await client.GetAsync($"/api/visitors/{visitor.Id}/qr")).Content.ReadAsStringAsync();
        Assert.Equal(await (await client.GetAsync($"/api/visitors/{Guid.NewGuid()}/qr")).Content.ReadAsStringAsync(), body);
        AssertNoVisitorData(body, visitor, "qr");
        await AssertVisitorUntouchedAsync(visitor);
    }

    // ----- managers: everything inside their own tenant ----------------------------------------

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingOwner")]
    [InlineData("BuildingManager")]
    public async Task Manager_DoesEverythingForTheirTenantsVisitors(string role)
    {
        var tenant = await CreateTenantAsync();
        var (managerId, client) = await UserWithRoleAsync(role, tenant);
        var (hostId, _) = await UserWithRoleAsync("User", tenant);
        var others = await SeedVisitorAsync(tenant, hostId);
        var email = NewGuestEmail();

        var register = await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, email, hostUserId: hostId,
            grants: new[] { Grant(DateTime.UtcNow, DateTime.UtcNow.AddHours(4)) }));

        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var row = Assert.Single(await VisitorRowsByEmailAsync(email));
        Assert.Equal(tenant, row.TenantId);
        Assert.Equal(hostId, row.HostUserId);
        Assert.Single(row.AccessGrants);

        // Hosted by someone else, still fully managed: list shows both, and every id action works.
        var ids = await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}"));
        Assert.Contains(others.Id, ids);
        Assert.Contains(row.Id, ids);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/visitors/{others.Id}")).StatusCode);
        var qr = await client.GetAsync($"/api/visitors/{others.Id}/qr");
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal(others.QrToken, (await JsonAsync(qr)).GetProperty("qrToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/visitors/{others.Id}/checkin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/visitors/{others.Id}/checkout", null)).StatusCode);
        Assert.Equal(VisitorStatus.CheckedOut, (await VisitorRowAsync(others.Id))!.Status);

        // No host named: the manager hosts the visitor themselves.
        var selfHosted = NewGuestEmail();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, selfHosted))).StatusCode);
        Assert.Equal(managerId, Assert.Single(await VisitorRowsByEmailAsync(selfHosted)).HostUserId);
    }

    [Fact]
    public async Task Manager_RegistersWithAHostWhoIsNotAMemberOfTheTenant_Gets400_AndNothingIsStored()
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("TenantAdmin", tenant);
        var (memberOfOther, _) = await UserWithRoleAsync("User", other);
        var (expiredMember, _) = await UserWithRoleAsync("User", tenant, expiresAt: DateTime.UtcNow.AddMinutes(-5));
        var (globalRoleOnly, _) = await UserWithRoleAsync("User", tenantId: null);
        var noRoleAtAll = await CreateUserAsync();

        foreach (var host in new[] { memberOfOther, expiredMember, globalRoleOnly, noRoleAtAll, Guid.NewGuid() })
        {
            var email = NewGuestEmail();
            var response = await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, email, hostUserId: host));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(await VisitorRowsByEmailAsync(email));
        }
    }

    [Fact]
    public async Task Register_AGrantThatEndsBeforeOrWhenItStarts_Gets400_AndNothingIsStored()
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("BuildingManager", tenant);
        var from = DateTime.UtcNow.AddHours(1);

        var sameInstant = NewGuestEmail();
        var reversed = NewGuestEmail();
        var oneBad = NewGuestEmail();
        var equal = await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, sameInstant, grants: new[] { Grant(from, from) }));
        var backwards = await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, reversed, grants: new[] { Grant(from, from.AddMinutes(-1)) }));
        var mixed = await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, oneBad,
            grants: new[] { Grant(from, from.AddHours(1)), Grant(from, from) }));

        Assert.Equal(HttpStatusCode.BadRequest, equal.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.Empty(await VisitorRowsByEmailAsync(sameInstant));
        Assert.Empty(await VisitorRowsByEmailAsync(reversed));
        Assert.Empty(await VisitorRowsByEmailAsync(oneBad));

        var good = NewGuestEmail();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, good,
            grants: new[] { Grant(from, from.AddMinutes(1)) }))).StatusCode);
        Assert.Single((await VisitorRowsByEmailAsync(good)).Single().AccessGrants);
    }

    [Fact]
    public async Task Manager_ListOfAnotherTenantIsRefused_AndOwnListNeverShowsOtherTenantsVisitors()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("BuildingOwner", own);
        var ownVisitor = await SeedVisitorAsync(own, await CreateUserAsync());
        var foreign = await SeedVisitorAsync(other, await CreateUserAsync());

        var ids = await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={own}"));

        Assert.Contains(ownVisitor.Id, ids);
        Assert.DoesNotContain(foreign.Id, ids);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/visitors?tenantId={other}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/visitors")).StatusCode);
    }

    // ----- paging -------------------------------------------------------------------------------

    [Fact]
    public async Task List_TakeIsCappedAt100_AndSkipAndDefaultsApply()
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("TenantAdmin", tenant);
        var host = await CreateUserAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            for (var i = 0; i < 105; i++)
            {
                db.Visitors.Add(new Visitor
                {
                    TenantId = tenant,
                    HostUserId = host,
                    Name = $"V{i}",
                    Email = $"v{i}-{Guid.NewGuid():N}@visitor-authz-bulk.test",
                    VisitDate = DateTime.UtcNow.AddDays(1).AddMinutes(i)
                });
            }
            await db.SaveChangesAsync();
        }

        Assert.Equal(100, (await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}&take=100000"))).Count);
        Assert.Equal(100, (await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}&take=101"))).Count);
        Assert.Equal(7, (await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}&take=7"))).Count);
        Assert.Equal(5, (await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}&skip=100&take=100"))).Count);
        Assert.Equal(20, (await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}"))).Count);
        Assert.Equal(20, (await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}&take=0"))).Count);
        Assert.Equal(20, (await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}&take=-5&skip=-3"))).Count);
    }

    // ----- SuperAdmin ---------------------------------------------------------------------------

    [Fact]
    public async Task SuperAdmin_DoesEverythingInAnyTenant_AndHostsWithoutBeingAMember()
    {
        var tenant = await CreateTenantAsync();
        var (superId, client) = await SuperAdminAsync();
        var (memberId, _) = await UserWithRoleAsync("User", tenant);
        var visitor = await SeedVisitorAsync(tenant, memberId);
        var selfHosted = NewGuestEmail();
        var memberHosted = NewGuestEmail();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, selfHosted))).StatusCode);
        Assert.Equal(superId, Assert.Single(await VisitorRowsByEmailAsync(selfHosted)).HostUserId);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, memberHosted, hostUserId: memberId))).StatusCode);

        Assert.Contains(visitor.Id, await VisitorIdsAsync(await client.GetAsync($"/api/visitors?tenantId={tenant}")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/visitors/{visitor.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/visitors/{visitor.Id}/qr")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/visitors/{visitor.Id}/checkin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/visitors/{visitor.Id}/checkout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/visitors/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_StillCannotNameAHostWhoIsNotAMemberOfTheTenant()
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await SuperAdminAsync();
        var outsider = await CreateUserAsync();
        var email = NewGuestEmail();

        var response = await client.PostAsJsonAsync("/api/visitors", RegisterBody(tenant, email, hostUserId: outsider));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await VisitorRowsByEmailAsync(email));
    }

    [Fact]
    public async Task SuperAdmin_ListWithoutATenant_Gets400()
    {
        var (_, client) = await SuperAdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/visitors")).StatusCode);
    }

    // ----- expired roles and tenant-pinned tokens -----------------------------------------------

    [Fact]
    public async Task ExpiredManagerRole_IsRefusedLikeAnOutsider()
    {
        var tenant = await CreateTenantAsync();
        var visitor = await SeedVisitorAsync(tenant, await CreateUserAsync());
        var (_, client) = await UserWithRoleAsync("TenantAdmin", tenant, expiresAt: DateTime.UtcNow.AddMinutes(-1));

        var statuses = await AllActionsAsync(client, tenant, visitor.Id);

        AssertAll(HttpStatusCode.Forbidden, statuses, 0, 1);
        AssertAll(HttpStatusCode.NotFound, statuses, 2, 3, 4, 5);
        await AssertVisitorUntouchedAsync(visitor);
    }

    [Fact]
    public async Task ExpiredMemberRole_CannotHostOrSeeTheirOwnVisitors()
    {
        var tenant = await CreateTenantAsync();
        var (hostId, client) = await UserWithRoleAsync("User", tenant, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var visitor = await SeedVisitorAsync(tenant, hostId);

        var statuses = await AllActionsAsync(client, tenant, visitor.Id);

        AssertAll(HttpStatusCode.Forbidden, statuses, 0, 1);
        AssertAll(HttpStatusCode.NotFound, statuses, 2, 3, 4, 5);
        await AssertVisitorUntouchedAsync(visitor);
    }

    [Fact]
    public async Task RoleThatExpiresInTheFuture_StillCounts()
    {
        var tenant = await CreateTenantAsync();
        var (_, client) = await UserWithRoleAsync("BuildingManager", tenant, expiresAt: DateTime.UtcNow.AddHours(1));
        var visitor = await SeedVisitorAsync(tenant, await CreateUserAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/visitors/{visitor.Id}")).StatusCode);
    }

    [Fact]
    public async Task ManagerOfTwoTenants_WithATokenPinnedToOne_OnlyActsInThatTenant()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var inA = await SeedVisitorAsync(tenantA, await CreateUserAsync());
        var inB = await SeedVisitorAsync(tenantB, await CreateUserAsync());
        var (userId, _) = await UserWithRoleAsync("BuildingManager", tenantA);
        await GrantRoleAsync(userId, "BuildingManager", tenantB);
        var pinnedToA = ClientAs(userId, new[] { "BuildingManager" }, tenantClaim: tenantA.ToString());

        Assert.Equal(HttpStatusCode.OK, (await pinnedToA.GetAsync($"/api/visitors/{inA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pinnedToA.GetAsync($"/api/visitors?tenantId={tenantA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pinnedToA.GetAsync($"/api/visitors/{inB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pinnedToA.GetAsync($"/api/visitors/{inB.Id}/qr")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pinnedToA.GetAsync($"/api/visitors?tenantId={tenantB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pinnedToA.PostAsJsonAsync("/api/visitors", RegisterBody(tenantB, NewGuestEmail()))).StatusCode);
    }

    [Fact]
    public async Task RoleClaimWithoutARoleRow_GrantsNothing()
    {
        // The token says TenantAdmin, but there is no UserRoles row: the claim alone is not membership.
        var tenant = await CreateTenantAsync();
        var visitor = await SeedVisitorAsync(tenant, await CreateUserAsync());
        var client = ClientAs(await CreateUserAsync(), new[] { "TenantAdmin" });

        var statuses = await AllActionsAsync(client, tenant, visitor.Id);

        AssertAll(HttpStatusCode.Forbidden, statuses, 0, 1);
        AssertAll(HttpStatusCode.NotFound, statuses, 2, 3, 4, 5);
        await AssertVisitorUntouchedAsync(visitor);
    }
}
