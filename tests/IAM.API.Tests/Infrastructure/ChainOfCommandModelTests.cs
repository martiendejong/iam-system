using IAM.Core.Authority;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Tests.Infrastructure;

/// <summary>Task 4843: Chain of Command data model, persistence and scope resolution.</summary>
public class ChainOfCommandModelTests
{
    private static IAMDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase("coc-" + Guid.NewGuid()).Options);

    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Domain = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private static AuthorityPolicy Policy(AuthorityScopeLevel level, string? project = null, string? resType = null,
        string? resId = null, string? action = null, bool over = false, DateTime? from = null) => new()
    {
        TenantId = Tenant, AuthorityDomainId = Domain, ScopeLevel = level, ProjectKey = project,
        ResourceType = resType, ResourceId = resId, Action = action, IsExplicitOverride = over,
        CommandChainId = Guid.NewGuid(), EffectiveFrom = from ?? Now.AddDays(-30)
    };

    private static AuthorityPolicy? Resolve(IEnumerable<AuthorityPolicy> ps, string? project = "p1",
        string? rt = "repo", string? rid = "r1", string? action = "deploy", DateTime? at = null) =>
        AuthorityPolicyResolver.Resolve(ps, Tenant, Domain, project, rt, rid, action, at ?? Now);

    [Fact]
    public void Narrowest_matching_scope_wins_when_no_override()
    {
        var org = Policy(AuthorityScopeLevel.Organization);
        var proj = Policy(AuthorityScopeLevel.Project, "p1");
        var act = Policy(AuthorityScopeLevel.Action, "p1", "repo", "r1", "deploy");
        Assert.Same(act, Resolve(new[] { org, proj, act }));
        Assert.Same(proj, Resolve(new[] { org, proj, act }, action: "other", rid: "r2"));
        Assert.Same(org, Resolve(new[] { org, proj, act }, project: "p2"));
    }

    [Fact]
    public void Explicit_override_at_wider_scope_beats_narrower_defaults()
    {
        var org = Policy(AuthorityScopeLevel.Organization, over: true);
        var act = Policy(AuthorityScopeLevel.Action, "p1", "repo", "r1", "deploy");
        Assert.Same(org, Resolve(new[] { org, act }));
    }

    [Fact]
    public void Disabled_expired_future_and_other_tenant_policies_are_ignored()
    {
        var disabled = Policy(AuthorityScopeLevel.Project, "p1"); disabled.IsEnabled = false;
        var expired = Policy(AuthorityScopeLevel.Project, "p1"); expired.EffectiveUntil = Now.AddDays(-1);
        var future = Policy(AuthorityScopeLevel.Project, "p1", from: Now.AddDays(1));
        var foreign = Policy(AuthorityScopeLevel.Project, "p1"); foreign.TenantId = Guid.NewGuid();
        var org = Policy(AuthorityScopeLevel.Organization);
        Assert.Same(org, Resolve(new[] { disabled, expired, future, foreign, org }));
        Assert.Null(Resolve(new[] { disabled, expired, future, foreign }));
    }

    [Fact]
    public void History_is_reconstructable_by_time()
    {
        var oldP = Policy(AuthorityScopeLevel.Project, "p1", from: Now.AddDays(-60)); oldP.EffectiveUntil = Now.AddDays(-10);
        var newP = Policy(AuthorityScopeLevel.Project, "p1", from: Now.AddDays(-10));
        Assert.Same(oldP, Resolve(new[] { oldP, newP }, at: Now.AddDays(-20)));
        Assert.Same(newP, Resolve(new[] { oldP, newP }, at: Now));
    }

    [Theory]
    [InlineData(AuthorityScopeLevel.Organization, null, null, null, null, true)]
    [InlineData(AuthorityScopeLevel.Organization, "p", null, null, null, false)]
    [InlineData(AuthorityScopeLevel.Project, "p", null, null, null, true)]
    [InlineData(AuthorityScopeLevel.Project, null, null, null, null, false)]
    [InlineData(AuthorityScopeLevel.Resource, "p", "t", "i", null, true)]
    [InlineData(AuthorityScopeLevel.Resource, "p", "t", null, null, false)]
    [InlineData(AuthorityScopeLevel.Action, "p", "t", "i", "a", true)]
    [InlineData(AuthorityScopeLevel.Action, "p", "t", "i", null, false)]
    public void Scope_columns_must_fit_level(AuthorityScopeLevel level, string? p, string? t, string? i, string? a, bool ok)
    {
        var policy = Policy(level, p, t, i, a);
        Assert.Equal(ok, AuthorityPolicyResolver.ValidateScope(policy) is null);
    }

    [Fact]
    public void Stage_member_needs_exactly_one_reference_matching_kind()
    {
        Assert.True(new CommandStageMember { Kind = CommandStageMemberKind.User, UserId = Guid.NewGuid() }.HasExactlyOneReferenceMatchingKind());
        Assert.True(new CommandStageMember { Kind = CommandStageMemberKind.Agent, ServiceAccountId = Guid.NewGuid() }.HasExactlyOneReferenceMatchingKind());
        Assert.True(new CommandStageMember { Kind = CommandStageMemberKind.ExternalAuthority, ExternalAuthorityRef = "notary:42" }.HasExactlyOneReferenceMatchingKind());
        Assert.False(new CommandStageMember { Kind = CommandStageMemberKind.Group, UserId = Guid.NewGuid() }.HasExactlyOneReferenceMatchingKind());
        Assert.False(new CommandStageMember { Kind = CommandStageMemberKind.User }.HasExactlyOneReferenceMatchingKind());
        Assert.False(new CommandStageMember { Kind = CommandStageMemberKind.User, UserId = Guid.NewGuid(), RoleId = Guid.NewGuid() }.HasExactlyOneReferenceMatchingKind());
    }

    [Fact]
    public async Task Chain_with_stages_members_policy_and_version_snapshot_round_trips()
    {
        var chainId = Guid.NewGuid();
        var name = "coc-" + Guid.NewGuid();
        await using (var ctx = NewContext())
        {
            var domain = new AuthorityDomain { TenantId = Tenant, Key = "deploys", Name = "Deploys" };
            var chain = new CommandChain { Id = chainId, TenantId = Tenant, Name = name };
            var stage = new CommandStage { Order = 1, Name = "Owner", EscalationTimeoutSeconds = 900 };
            stage.Members.Add(new CommandStageMember { Kind = CommandStageMemberKind.ExternalAuthority, ExternalAuthorityRef = "regulator:x" });
            chain.Stages.Add(stage);
            domain.Policies.Add(new AuthorityPolicy
            {
                TenantId = Tenant, CommandChainId = chainId, ContinuityPolicyRef = "continuity:7", ProjectKey = "p1",
                ScopeLevel = AuthorityScopeLevel.Project
            });
            ctx.AddRange(domain, chain);
            ctx.Add(new AuthorityRecordVersion { TenantId = Tenant, RecordType = "CommandChain", RecordId = chainId, Version = 1, Snapshot = "{}" });
            await ctx.SaveChangesAsync();
        }

        await using var read = NewContext();
        // The InMemory store is private to the first context, so check the uniqueness rules on the model.
        var et = read.Model.FindEntityType(typeof(CommandStage))!;
        Assert.Contains(et.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "CommandChainId", "Order" }));
        Assert.Contains(read.Model.FindEntityType(typeof(AuthorityRecordVersion))!.GetIndexes(),
            i => i.IsUnique && i.Properties.Count == 3);
        Assert.Contains(read.Model.FindEntityType(typeof(AuthorityDomain))!.GetIndexes(),
            i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "Key" }));
    }
}
