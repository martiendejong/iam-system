using Hazina.Security.ApiKeys;
using Jengo.Mcp;

namespace IAM.API.Mcp;

/// <summary>
/// IAM's wiring onto the shared Jengo MCP template (scp-jengo/jengo-mcp-template, package
/// Jengo.Mcp.AspNetCore) for task 5800: a remote MCP server at <c>/mcp</c> exposing agent-safe
/// user-onboarding tools. The template provides the streamable-http transport, an additive auth
/// scheme for <c>/mcp</c> (fully independent of the app's JWT bearer / IAM.Session cookie / API-key
/// schemes), the RFC 9728 401-challenge with <c>/.well-known/oauth-protected-resource</c>, and it
/// enforces auth on <c>/mcp</c>. Everything app-specific is here: an API-key validator bound to
/// IAM users and the tool provider. Call <c>MapJengoMcp()</c> after UseAuthentication /
/// UseAuthorization to expose the endpoint.
///
/// Deliberate policy (see docs/mcp-server.md): the tool surface onboards people through
/// invitations only. There is NO tool that creates a user with a password, sets / changes /
/// resets a password, or mints tokens — a leaked agent key must never be able to take over or
/// impersonate an account.
/// </summary>
public static class IamMcpExtensions
{
    public static IServiceCollection AddIamMcp(this IServiceCollection services)
    {
        services.AddJengoMcp(options =>
        {
            options.ServerName = "Jengo IAM";
            options.ServerVersion = "1.0.0";
            options.Instructions =
                "Agent-safe user onboarding for the Jengo IAM system. Read users, roles and " +
                "tenants (list_users / get_user / list_roles / list_tenants), onboard people via " +
                "invitations (send_invitation — the invited person sets their own password " +
                "through the accept link; list_invitations / revoke_invitation manage them) and " +
                "manage role assignments (assign_role / remove_role). By design there is no tool " +
                "to create a user with a password, change or reset a password, or mint tokens.";
            options.Auth.ResourceMetadata.ScopesSupported.Add("iam");
        })
            // API keys from the Mcp:Keys configuration array, each bound to one IAM user id.
            .AddApiKeyValidator<IamMcpApiKeyValidator>()
            // The 9 onboarding tools, each acting as (and audited against) the key's user.
            .AddToolProvider<IamMcpToolProvider>();

        // The Hazina X-Api-Key middleware (UseHazinaApiKeyAuth, before UseAuthentication) 401s any
        // request whose X-Api-Key header it does not recognise — and MCP clients present the MCP
        // key in that very header. Exempt /mcp: the endpoint is still never open, because the
        // template enforces its own auth scheme (this validator) on everything under /mcp.
        services.Configure<HazinaApiKeyOptions>(o => o.ExcludedPathPrefixes.Add("/mcp"));

        return services;
    }
}
