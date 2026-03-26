using Microsoft.Extensions.DependencyInjection;

namespace IAM.SDK.DotNet;

/// <summary>
/// Extension methods for registering IAM SDK services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add IAM SDK services to the dependency injection container
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Configuration action for IAM client options</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddIamClient(
        this IServiceCollection services,
        Action<IamClientOptions> configureOptions)
    {
        services.Configure(configureOptions);

        services.AddHttpClient<IIamAuthClient, IamAuthClient>();

        return services;
    }

    /// <summary>
    /// Add IAM SDK services with options from configuration
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="apiBaseUrl">Base URL of the IAM API</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddIamClient(
        this IServiceCollection services,
        string apiBaseUrl)
    {
        return services.AddIamClient(options =>
        {
            options.ApiBaseUrl = apiBaseUrl;
        });
    }
}
