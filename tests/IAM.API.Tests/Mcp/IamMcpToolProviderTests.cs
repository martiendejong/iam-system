using IAM.API.Mcp;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace IAM.API.Tests.Mcp;

/// <summary>
/// Pins the IAM MCP tool surface (task 5800). The policy behind this server is that an agent key
/// can onboard people ONLY through invitations: the invited person proves mailbox control and
/// chooses their own password on the accept page. So the tool list must contain exactly the 9
/// agreed tools, and must NEVER grow a tool that creates a user with a password, sets / changes /
/// resets a password, or mints tokens. If a future change adds such a tool, this test is the
/// tripwire — do not "fix" the test; remove the tool.
/// </summary>
public class IamMcpToolProviderTests
{
    private static readonly string[] ExpectedTools =
    {
        "list_users",
        "get_user",
        "list_roles",
        "list_tenants",
        "list_invitations",
        "send_invitation",
        "revoke_invitation",
        "assign_role",
        "remove_role",
    };

    private static readonly string[] ForbiddenTools =
    {
        "create_user",
        "set_password",
        "change_password",
        "reset_password",
        "mint_token",
    };

    private static IamMcpToolProvider CreateProvider()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase($"iam-mcp-tools-{Guid.NewGuid()}")
            .Options;
        var db = new IAMDbContext(options);
        var configuration = new ConfigurationBuilder().Build();
        return new IamMcpToolProvider(db, new StubInvitationService(), configuration, NullLogger<IamMcpToolProvider>.Instance);
    }

    private static async Task<List<string>> ListToolNamesAsync()
    {
        var provider = CreateProvider();
        // ListToolsAsync does not touch the context (the catalogue is identical for every caller).
        var tools = await provider.ListToolsAsync(null!, CancellationToken.None);
        return tools.Select(t => t.Name).ToList();
    }

    [Fact]
    public async Task ToolList_ContainsExactlyTheNineOnboardingTools()
    {
        var names = await ListToolNamesAsync();

        Assert.Equal(ExpectedTools.OrderBy(n => n), names.OrderBy(n => n));
        foreach (var expected in ExpectedTools)
            Assert.Contains(expected, names);
    }

    [Fact]
    public async Task ToolList_ContainsNoPasswordOrTokenCapableTools()
    {
        var names = await ListToolNamesAsync();

        foreach (var forbidden in ForbiddenTools)
            Assert.DoesNotContain(forbidden, names);

        // Belt-and-braces: nothing password/token-shaped under any other name either.
        foreach (var name in names)
        {
            Assert.DoesNotContain("password", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TryCallTool_UnknownTool_ReturnsNullSoOtherProvidersGetTheCall()
    {
        var provider = CreateProvider();

        var result = await provider.TryCallToolAsync(
            null!, "create_user",
            new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>Never invoked by these tests; present only to satisfy the constructor.</summary>
    private sealed class StubInvitationService : IInvitationService
    {
        public Task<InvitationSendResult> SendInvitationAsync(string email, Guid tenantId, Guid roleId, Guid invitedByUserId, int? expiryDays = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<BulkInviteResult> SendBulkInvitationsAsync(IEnumerable<BulkInviteEntry> entries, Guid tenantId, Guid invitedByUserId, bool callerIsSuperAdmin, CancellationToken ct = default, bool callerIsTenantOwner = false)
            => throw new NotSupportedException();

        public Task<InvitationAcceptResult> AcceptInvitationAsync(string token, string? password, string? firstName, string? lastName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> RevokeInvitationAsync(Guid invitationId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<Invitation>> GetInvitationsByTenantAsync(Guid tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<Invitation>> GetPendingInvitationsAsync(Guid tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Invitation?> GetInvitationByTokenAsync(string token, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
