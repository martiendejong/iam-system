using Microsoft.Extensions.DependencyInjection;

namespace IAM.SDK.DotNet;

/// <summary>
/// Extension methods for registering IAM device client services
/// </summary>
public static class DeviceServiceCollectionExtensions
{
    /// <summary>
    /// Add IAM device client services to the dependency injection container
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Configuration action for device client options</param>
    /// <returns>Service collection for chaining</returns>
    /// <example>
    /// <code>
    /// services.AddIamDeviceClient(options =>
    /// {
    ///     options.BaseUrl = "https://iam.example.com";
    ///     options.DeviceId = "sensor-001";
    ///     options.HeartbeatIntervalSeconds = 30;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddIamDeviceClient(
        this IServiceCollection services,
        Action<IamDeviceClientOptions> configureOptions)
    {
        services.Configure(configureOptions);

        services.AddHttpClient<IamDeviceClient>();

        return services;
    }

    /// <summary>
    /// Add IAM device client services with base URL and device ID
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="baseUrl">Base URL of the IAM API</param>
    /// <param name="deviceId">Unique device identifier</param>
    /// <returns>Service collection for chaining</returns>
    /// <example>
    /// <code>
    /// services.AddIamDeviceClient("https://iam.example.com", "sensor-001");
    /// </code>
    /// </example>
    public static IServiceCollection AddIamDeviceClient(
        this IServiceCollection services,
        string baseUrl,
        string deviceId)
    {
        return services.AddIamDeviceClient(options =>
        {
            options.BaseUrl = baseUrl;
            options.DeviceId = deviceId;
        });
    }
}
