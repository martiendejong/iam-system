using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4764: SCIM group create, replace and patch(add) only add users of the token's own tenant (a non-expired
/// UserRoles row for it, or a successful SCIM Create in its log, as for the user endpoints of task 4699); any other
/// id is skipped exactly like an unknown id. Group get and list show only in-tenant members, so a membership that
/// is already stored for a foreign user is never shown.
/// </summary>
public class ScimGroupMemberTenantScopeTests
{
    private sealed class World
    {
        public required DbContextOptions<IAMDbContext> Options { get; init; }
        public required IAMDbContext Db { get; init; }
        public required ScimService Service { get; init; }
        public required Tenant A { get; init; }
        public required Tenant B { get; init; }
        public required User MemberA { get; init; }
        public required User SecondMemberA { get; init; }
        public required User MemberB { get; init; }

        public IAMDbContext NewReadContext() => new(Options);
    }

    private static World CreateWorld()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new IAMDbContext(options);
        var a = new Tenant { Id = Guid.NewGuid(), Name = $"A {Guid.NewGuid():N}", IsActive = true };
        var b = new Tenant { Id = Guid.NewGuid(), Name = $"B {Guid.NewGuid():N}", IsActive = true };
        var resident = new Role { Id = Guid.NewGuid(), Name = "Resident", Permissions = "[]" };
        db.Tenants.AddRange(a, b);
        db.Roles.Add(resident);

        User NewUser(string label) => new()
        {
            Id = Guid.NewGuid(), Email = $"{label}-{Guid.NewGuid():N}@scim4764.test", FirstName = label, LastName = "Person",
            PasswordHash = "x", IsActive = true
        };
        var memberA = NewUser("membera");
        var secondA = NewUser("secondmembera");
        var memberB = NewUser("memberb");
        db.Users.AddRange(memberA, secondA, memberB);
        db.UserRoles.AddRange(
            new UserRole { Id = Guid.NewGuid(), UserId = memberA.Id, RoleId = resident.Id, TenantId = a.Id, GrantedAt = DateTime.UtcNow },
            new UserRole { Id = Guid.NewGuid(), UserId = secondA.Id, RoleId = resident.Id, TenantId = a.Id, GrantedAt = DateTime.UtcNow },
            new UserRole { Id = Guid.NewGuid(), UserId = memberB.Id, RoleId = resident.Id, TenantId = b.Id, GrantedAt = DateTime.UtcNow });
        db.SaveChanges();

        return new World
        {
            Options = options, Db = db, Service = new ScimService(db), A = a, B = b,
            MemberA = memberA, SecondMemberA = secondA, MemberB = memberB
        };
    }

    private static ScimGroupResource Body(params Guid[] memberIds) => new()
    {
        DisplayName = $"group-{Guid.NewGuid():N}",
        Members = memberIds.Select(id => new ScimMember { Value = id.ToString() }).ToList()
    };

    private static ScimPatchRequest AddMembers(params Guid[] memberIds) => new()
    {
        Operations =
        {
            new ScimPatchOperation
            {
                Op = "add", Path = "members",
                Value = JsonSerializer.SerializeToElement(memberIds.Select(id => new { value = id.ToString() }).ToArray())
            }
        }
    };

    private static List<Guid> MemberIds(ScimGroupResource group) =>
        (group.Members ?? new List<ScimMember>()).Select(m => Guid.Parse(m.Value)).ToList();

    private static async Task<List<Guid>> StoredMemberIdsAsync(World w, string groupId)
    {
        using var read = w.NewReadContext();
        var id = Guid.Parse(groupId);
        return await read.GroupMemberships.Where(m => m.GroupId == id).Select(m => m.UserId).ToListAsync();
    }

    // ----- create -------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_SkipsAUserOfAnotherTenant_AndKeepsTheInTenantOne()
    {
        var w = CreateWorld();

        var created = await w.Service.CreateGroupAsync(w.A.Id, Body(w.MemberA.Id, w.MemberB.Id));

        Assert.Equal(new[] { w.MemberA.Id }, MemberIds(created));
        Assert.Equal(new[] { w.MemberA.Id }, await StoredMemberIdsAsync(w, created.Id!));
    }

    [Fact]
    public async Task Create_ForeignAndUnknownIdsLookTheSame_NothingIsStored()
    {
        var w = CreateWorld();

        var foreign = await w.Service.CreateGroupAsync(w.A.Id, Body(w.MemberB.Id));
        var unknown = await w.Service.CreateGroupAsync(w.A.Id, Body(Guid.NewGuid()));

        Assert.Empty(MemberIds(foreign));
        Assert.Empty(MemberIds(unknown));
        Assert.Empty(await StoredMemberIdsAsync(w, foreign.Id!));
        Assert.Empty(await StoredMemberIdsAsync(w, unknown.Id!));
    }

    [Fact]
    public async Task Create_AUserProvisionedThroughThisTenantsScimLog_CountsAsInTenant()
    {
        var w = CreateWorld();
        var provisioned = new User
        {
            Id = Guid.NewGuid(), Email = $"prov-{Guid.NewGuid():N}@scim4764.test", FirstName = "P", LastName = "P", PasswordHash = "x", IsActive = true
        };
        w.Db.Users.Add(provisioned);
        w.Db.ScimProvisioningLogs.Add(new ScimProvisioningLog
        {
            TenantId = w.A.Id, Operation = "Create", ResourceType = "User", ResourceId = provisioned.Id, Status = "Success"
        });
        w.Db.SaveChanges();

        var created = await w.Service.CreateGroupAsync(w.A.Id, Body(provisioned.Id));

        Assert.Equal(new[] { provisioned.Id }, MemberIds(created));
    }

    // ----- replace ------------------------------------------------------------------------------------

    [Fact]
    public async Task Replace_SkipsAUserOfAnotherTenant_AndKeepsTheInTenantOnes()
    {
        var w = CreateWorld();
        var group = await w.Service.CreateGroupAsync(w.A.Id, Body(w.MemberA.Id));

        var replaced = await w.Service.ReplaceGroupAsync(w.A.Id, Guid.Parse(group.Id!), Body(w.SecondMemberA.Id, w.MemberB.Id));

        Assert.Equal(new[] { w.SecondMemberA.Id }, MemberIds(replaced));
        Assert.Equal(new[] { w.SecondMemberA.Id }, await StoredMemberIdsAsync(w, group.Id!));
    }

    // ----- patch (add) --------------------------------------------------------------------------------

    [Fact]
    public async Task PatchAdd_SkipsAUserOfAnotherTenant_AndAddsTheInTenantOne()
    {
        var w = CreateWorld();
        var group = await w.Service.CreateGroupAsync(w.A.Id, Body());

        var patched = await w.Service.PatchGroupAsync(w.A.Id, Guid.Parse(group.Id!), AddMembers(w.MemberB.Id, w.MemberA.Id));

        Assert.Equal(new[] { w.MemberA.Id }, MemberIds(patched));
        Assert.Equal(new[] { w.MemberA.Id }, await StoredMemberIdsAsync(w, group.Id!));
    }

    [Fact]
    public async Task PatchAdd_OnlyAForeignUser_StoresNothing()
    {
        var w = CreateWorld();
        var group = await w.Service.CreateGroupAsync(w.A.Id, Body());

        var patched = await w.Service.PatchGroupAsync(w.A.Id, Guid.Parse(group.Id!), AddMembers(w.MemberB.Id));

        Assert.Empty(MemberIds(patched));
        Assert.Empty(await StoredMemberIdsAsync(w, group.Id!));
    }

    // ----- get and list with a pre-existing foreign membership -----------------------------------------

    private static async Task<Guid> GroupWithStoredForeignMembershipAsync(World w)
    {
        // Written before the fix: the foreign membership is already in the table.
        var group = new Group { Id = Guid.NewGuid(), Name = $"old-{Guid.NewGuid():N}", TenantId = w.A.Id, GroupType = "scim" };
        w.Db.Groups.Add(group);
        w.Db.GroupMemberships.AddRange(
            new GroupMembership { GroupId = group.Id, UserId = w.MemberA.Id, Role = "member" },
            new GroupMembership { GroupId = group.Id, UserId = w.MemberB.Id, Role = "member" });
        await w.Db.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task Get_ShowsOnlyInTenantMembers_EvenWhenAForeignMembershipIsStored()
    {
        var w = CreateWorld();
        var groupId = await GroupWithStoredForeignMembershipAsync(w);

        var group = await w.Service.GetGroupAsync(w.A.Id, groupId);

        Assert.Equal(new[] { w.MemberA.Id }, MemberIds(group!));
    }

    [Fact]
    public async Task List_ShowsOnlyInTenantMembers_EvenWhenAForeignMembershipIsStored()
    {
        var w = CreateWorld();
        var groupId = await GroupWithStoredForeignMembershipAsync(w);

        var list = await w.Service.ListGroupsAsync(w.A.Id, new ScimQueryOptions { Count = 100 });

        var group = Assert.Single(list.Resources, g => g.Id == groupId.ToString());
        Assert.Equal(new[] { w.MemberA.Id }, MemberIds(group));
    }

    // ----- regression: in-tenant members work on every operation ---------------------------------------

    [Fact]
    public async Task InTenantMembers_StillWorkOnCreateReplaceAndPatch()
    {
        var w = CreateWorld();

        var created = await w.Service.CreateGroupAsync(w.A.Id, Body(w.MemberA.Id));
        Assert.Equal(new[] { w.MemberA.Id }, MemberIds(created));

        var replaced = await w.Service.ReplaceGroupAsync(w.A.Id, Guid.Parse(created.Id!), Body(w.SecondMemberA.Id));
        Assert.Equal(new[] { w.SecondMemberA.Id }, MemberIds(replaced));

        var patched = await w.Service.PatchGroupAsync(w.A.Id, Guid.Parse(created.Id!), AddMembers(w.MemberA.Id));
        Assert.Equal(new[] { w.MemberA.Id, w.SecondMemberA.Id }.OrderBy(i => i), MemberIds(patched).OrderBy(i => i));

        var fetched = await w.Service.GetGroupAsync(w.A.Id, Guid.Parse(created.Id!));
        Assert.Equal(2, MemberIds(fetched!).Count);
    }
}
