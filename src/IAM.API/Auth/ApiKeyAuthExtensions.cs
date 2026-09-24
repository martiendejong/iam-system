using Hazina.Security.ApiKeys;
using IAM.Infrastructure.Services;

namespace IAM.API.Auth;

/// <summary>
/// IAM's wiring of the shared Hazina.Security.ApiKeys module (replaces IAM's former private
/// ApiKeyAuthenticationMiddleware). IAM is the authority for keys: its own table is the source, and
/// other Jengo apps validate against it through <c>POST /api/api-keys/introspect</c>.
/// </summary>
public static class ApiKeyAuthExtensions
{
    public static IServiceCollection AddIamApiKeyAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("ApiKeys");

        var builder = services
            .AddHazinaApiKeyAuth(o =>
            {
                o.KeyPrefixBase = "iam";
                // Keys without their own limit keep IAM's existing authenticated-caller budget.
                o.DefaultRequestsPerMinute = configuration.GetValue("RateLimiting:AuthenticatedLimitPerMinute", 300);
                section.GetSection("Cache").Bind(o);
            })
            .UseStore<EfApiKeyStore>()
            .UseUsageRecorder<EfApiKeyStore>();

        // Raw keys are archived in Vault. Without a configured Vault, existing keys keep validating but issuing or
        // rotating one fails closed; only local development falls back to an in-memory (non-durable) vault.
        if (!string.IsNullOrWhiteSpace(section["Vault:BaseUrl"]))
            builder.UseVault(o => section.GetSection("Vault").Bind(o));
        else if (environment.IsDevelopment())
            builder.UseInMemorySecretVault();
        else
            builder.UseUnconfiguredSecretVault();

        return services;
    }
}
