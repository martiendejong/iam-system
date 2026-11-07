using IAM.Core.Authority;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAM.API.Tests.Infrastructure;

/// <summary>Task 4844: AuthorityResolverService loads real facts (groups, roles, SoD, availability) per tenant.</summary>
public class AuthorityResolverServiceTests
{
    private sealed class FakeAuthz : IUnifiedAuthorizationService
    {
        public AuthorizationDecision Decision { get; set; } = new() { Allowed = true, Reason = "ok" };
        public AuthorizationRequest? Last { get; private set; }
        public Task<AuthorizationDecision> EvaluateAsync(AuthorizationRequest request, CancellationToken ct = default)
        { Last = request; return Task.FromResult(Decision); }
        public Task<List<AuthorizationDecision>> EvaluateBatchAsync(List<AuthorizationRequest> requests, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(string principalType, Guid principalId, Guid tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private static IAMDbContext NewDb() =>
        new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase("arv-" + Guid.NewGuid()).Options);

    private static User NewUser(IAMDbContext db, string name, bool active = true)
    {
        var u = new User { Email = name + "@authority.test", FirstName = name, LastName = "T", IsActive = active, EmailConfirmed = true };
        db.Users.Add(u);
        return u;
    }

    private static (AuthorityDomain domain, AuthorityPolicy policy, CommandChain chain) Seed(
        IAMDbContext db, Guid tenant, Action<CommandChain> stages, string? continuity = null)
    {
        var tenantRow = new Tenant { Id = tenant, Name = "T" + tenant, IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Tenants.Add(tenantRow);
        var domain = new AuthorityDomain { TenantId = tenant, Key = "deploys", Name = "Deploys" };
        var chain = new CommandChain { TenantId = tenant, Name = "c", EffectiveFrom = Now.AddYears(-1) };
        stages(chain);
        var policy = new AuthorityPolicy
        {
            TenantId = tenant, AuthorityDomainId = domain.Id, CommandChainId = chain.Id,
            ScopeLevel = AuthorityScopeLevel.Organization, EffectiveFrom = Now.AddYears(-1), ContinuityPolicyRef = continuity
        };
        db.AddRange(domain, chain, policy);
        return (domain, policy, chain);
    }

    private static AuthorityRequest Req(Guid tenant, Guid actor, Action<AuthorityRequestContext>? ctx = null)
    {
        var r = new AuthorityRequest { TenantId = tenant, DomainKey = "deploys", ActorUserId = actor, AtUtc = Now, ProjectKey = "p1" };
        ctx?.Invoke(r.Context);
        return r;
    }

    [Fact]
    public async Task Tenants_use_their_own_chains_and_group_members_are_expanded()
    {
        await using var db = NewDb();
        var pg = Guid.NewGuid(); var per = Guid.NewGuid();
        var requester = NewUser(db, "req"); var lead = NewUser(db, "lead"); var gm1 = NewUser(db, "gm1"); var gm2 = NewUser(db, "gm2");
        var groupId = Guid.NewGuid();

        Seed(db, pg, c => c.Stages.Add(new CommandStage { Order = 1, Name = "Lead", Members = { new() { Kind = CommandStageMemberKind.User, UserId = lead.Id } } }));
        Seed(db, per, c => c.Stages.Add(new CommandStage
        {
            Order = 1, Name = "Board", RequiredApprovals = 2,
            Members = { new() { Kind = CommandStageMemberKind.Group, GroupId = groupId } }
        }));
        db.GroupMemberships.AddRange(
            new GroupMembership { GroupId = groupId, UserId = gm1.Id, IsActive = true },
            new GroupMembership { GroupId = groupId, UserId = gm2.Id, IsActive = true });
        await db.SaveChangesAsync();

        var svc = new AuthorityResolverService(db, new FakeAuthz());
        var rPg = await svc.ResolveAsync(Req(pg, requester.Id));
        var rPer = await svc.ResolveAsync(Req(per, requester.Id));

        Assert.Equal(AuthorityDecisionState.RequiresApproval, rPg.State);
        Assert.Equal(lead.Id, rPg.Authorities.Single().UserId);
        Assert.Equal(AuthorityDecisionState.RequiresApproval, rPer.State);
        Assert.Equal(2, rPer.RequiredApprovals);
        Assert.Equal(new[] { gm1.Id, gm2.Id }.OrderBy(x => x), rPer.Authorities.Select(a => a.UserId!.Value).OrderBy(x => x));
        Assert.NotEqual(rPg.Policy!.PolicyId, rPer.Policy!.PolicyId);
    }

    [Fact]
    public async Task Role_holders_decide_and_disabled_users_are_ignored()
    {
        await using var db = NewDb();
        var t = Guid.NewGuid();
        var requester = NewUser(db, "req"); var ok = NewUser(db, "ok"); var off = NewUser(db, "off", active: false);
        var role = new Role { Name = "Approver" };
        db.Roles.Add(role);
        Seed(db, t, c => c.Stages.Add(new CommandStage { Order = 1, Name = "Roles", Members = { new() { Kind = CommandStageMemberKind.Role, RoleId = role.Id } } }));
        db.UserRoles.AddRange(new UserRole { UserId = ok.Id, RoleId = role.Id, TenantId = t }, new UserRole { UserId = off.Id, RoleId = role.Id, TenantId = t });
        await db.SaveChangesAsync();

        var r = await new AuthorityResolverService(db, new FakeAuthz()).ResolveAsync(Req(t, requester.Id));
        Assert.Equal(ok.Id, r.Authorities.Single().UserId);
    }

    [Fact]
    public async Task Blocking_sod_constraint_and_availability_evidence_move_the_decision_up()
    {
        await using var db = NewDb();
        var t = Guid.NewGuid();
        var requester = NewUser(db, "req"); var lead = NewUser(db, "lead"); var director = NewUser(db, "dir");
        var requesterRole = new Role { Name = "Requester" }; var approverRole = new Role { Name = "Approver" };
        db.Roles.AddRange(requesterRole, approverRole);
        Seed(db, t, c =>
        {
            c.Stages.Add(new CommandStage { Order = 1, Name = "Lead", Members = { new() { Kind = CommandStageMemberKind.User, UserId = lead.Id } } });
            c.Stages.Add(new CommandStage { Order = 2, Name = "Director", Members = { new() { Kind = CommandStageMemberKind.User, UserId = director.Id } } });
        });
        db.UserRoles.AddRange(
            new UserRole { UserId = requester.Id, RoleId = requesterRole.Id, TenantId = t },
            new UserRole { UserId = lead.Id, RoleId = approverRole.Id, TenantId = t });
        db.SodConstraints.Add(new SodConstraint
        {
            TenantId = t, Name = "Requester vs Approver", ConflictingRoleA = requesterRole.Id, ConflictingRoleB = approverRole.Id,
            Severity = SodSeverity.Block, IsActive = true
        });
        await db.SaveChangesAsync();

        var svc = new AuthorityResolverService(db, new FakeAuthz());
        var sod = await svc.ResolveAsync(Req(t, requester.Id));
        Assert.Equal(AuthorityDecisionState.Escalate, sod.State);
        Assert.Equal(director.Id, sod.Authorities.Single().UserId);
        Assert.Contains(sod.Reasons, x => x.Code == "SOD_APPROVER_EXCLUDED");

        // Without SoD, the lead is reached; known-unavailable evidence then pushes to the director.
        db.SodConstraints.RemoveRange(db.SodConstraints);
        db.AuthorityAvailabilityEvidence.Add(new AuthorityAvailabilityEvidence
        {
            TenantId = t, UserId = lead.Id, Source = "heartbeat", EvidenceRef = "hb-1", IsAvailable = false, ObservedAt = Now.AddMinutes(-5)
        });
        await db.SaveChangesAsync();
        var away = await svc.ResolveAsync(Req(t, requester.Id));
        Assert.Equal(AuthorityDecisionState.Escalate, away.State);
        Assert.Equal(director.Id, away.Authorities.Single().UserId);
    }

    [Fact]
    public async Task Explicit_deny_from_the_authorization_engine_is_a_hard_deny_and_target_is_passed_through()
    {
        await using var db = NewDb();
        var t = Guid.NewGuid();
        var requester = NewUser(db, "req"); var lead = NewUser(db, "lead");
        Seed(db, t, c => c.Stages.Add(new CommandStage { Order = 1, Name = "Lead", Members = { new() { Kind = CommandStageMemberKind.User, UserId = lead.Id } } }));
        await db.SaveChangesAsync();

        var authz = new FakeAuthz { Decision = new AuthorizationDecision { Allowed = false, MatchedPolicy = "No prod deploys", Reason = "denied by rule" } };
        var req = Req(t, requester.Id);
        req.ResourceType = "repo"; req.ResourceId = "r1"; req.Action = "deploy";
        var r = await new AuthorityResolverService(db, authz).ResolveAsync(req);

        Assert.Equal(AuthorityDecisionState.Deny, r.State);
        Assert.Contains(r.Reasons, x => x.Code == "RBAC_EXPLICIT_DENY");
        Assert.Equal("p1:repo:r1", authz.Last!.Resource);
        Assert.Equal("deploy", authz.Last.Action);
    }

    [Fact]
    public async Task Unknown_domain_unknown_or_disabled_actor_deny()
    {
        await using var db = NewDb();
        var t = Guid.NewGuid();
        var off = NewUser(db, "off", active: false); var ok = NewUser(db, "ok");
        Seed(db, t, c => c.Stages.Add(new CommandStage { Order = 1, Name = "S", Members = { new() { Kind = CommandStageMemberKind.User, UserId = ok.Id } } }));
        await db.SaveChangesAsync();
        var svc = new AuthorityResolverService(db, new FakeAuthz());

        var noDomain = Req(t, ok.Id); noDomain.DomainKey = "nope";
        Assert.Contains((await svc.ResolveAsync(noDomain)).Reasons, x => x.Code == "DOMAIN_NOT_FOUND");
        Assert.Contains((await svc.ResolveAsync(Req(t, off.Id))).Reasons, x => x.Code == "ACTOR_UNKNOWN_OR_INACTIVE");
        Assert.Equal(AuthorityDecisionState.Deny, (await svc.ResolveAsync(Req(t, Guid.NewGuid()))).State);
        // A domain key of another tenant is invisible here.
        Assert.Equal(AuthorityDecisionState.Deny, (await svc.ResolveAsync(Req(Guid.NewGuid(), ok.Id))).State);
    }
}
