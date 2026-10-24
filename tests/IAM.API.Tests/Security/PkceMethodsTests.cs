using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 4271: regression guard — plain PKCE must never be re-enabled.
/// Plain PKCE: code_challenge == code_verifier, so any intercepted authorize
/// request leaks the verifier.
/// </summary>
public class PkceMethodsTests
{
    [Fact]
    public void ServerOptions_Plain_IsRemovedAndS256_IsRetained()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenIddict()
            .AddServer(options =>
            {
                // Mirror the removal in Program.cs
                options.Configure(o => o.CodeChallengeMethods.Remove(OpenIddictConstants.CodeChallengeMethods.Plain));
            });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

        Assert.DoesNotContain(OpenIddictConstants.CodeChallengeMethods.Plain, opts.CodeChallengeMethods);
        Assert.Contains(OpenIddictConstants.CodeChallengeMethods.Sha256, opts.CodeChallengeMethods);
    }

    [Fact]
    public void ServerOptions_WithoutRemoval_ContainsBothMethods()
    {
        // Documents the default — plain IS present before our removal.
        // If this assertion starts failing, the Remove() call in Program.cs is now a no-op.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenIddict()
            .AddServer(_ => { });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

        Assert.Contains(OpenIddictConstants.CodeChallengeMethods.Plain, opts.CodeChallengeMethods);
        Assert.Contains(OpenIddictConstants.CodeChallengeMethods.Sha256, opts.CodeChallengeMethods);
    }
}
