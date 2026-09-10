using IAM.EdgeGateway;
using IAM.SDK.DotNet;

var builder = Host.CreateApplicationBuilder(args);

// Bind configuration
var iamSection = builder.Configuration.GetSection("IAM");
var gatewayOptions = new EdgeGatewayOptions();
iamSection.Bind(gatewayOptions);

// Validate SharedSecret at startup — fail fast in production if not properly configured
var sharedSecret = gatewayOptions.SharedSecret;
if (sharedSecret == "configure-in-production" || sharedSecret.Length < 32)
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException(
            "Gateway SharedSecret is not configured or is too short. " +
            "Set a random string of at least 32 characters in configuration.");
    else
        Console.Error.WriteLine("WARNING: EdgeGateway SharedSecret is using development default or is too short.");
}

// Register the IamDeviceClient from the SDK for gateway authentication
builder.Services.AddIamDeviceClient(options =>
{
    options.BaseUrl = gatewayOptions.BaseUrl;
    options.DeviceId = gatewayOptions.GatewayDeviceId;
    options.TimeoutSeconds = 30;
    options.HeartbeatIntervalSeconds = gatewayOptions.HealthCheckIntervalSeconds;
});

// Register a named HttpClient for direct API calls (claims endpoint)
builder.Services.AddHttpClient("IAM.EdgeGateway", client =>
{
    client.BaseAddress = new Uri(gatewayOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Register edge gateway options as a singleton
builder.Services.AddSingleton(gatewayOptions);

// Register the authorization cache as a singleton (shared across all components)
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<EdgeGatewayOptions>();
    return new EdgeAuthorizationCache(
        claimsTtl: TimeSpan.FromMinutes(options.CacheClaimsTtlMinutes),
        decisionTtl: TimeSpan.FromMinutes(options.CacheDecisionTtlMinutes));
});

// Register the edge gateway service
builder.Services.AddSingleton(sp =>
{
    var iamClient = sp.GetRequiredService<IamDeviceClient>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("IAM.EdgeGateway");
    var cache = sp.GetRequiredService<EdgeAuthorizationCache>();
    var logger = sp.GetRequiredService<ILogger<EdgeGatewayService>>();
    var options = sp.GetRequiredService<EdgeGatewayOptions>();

    return new EdgeGatewayService(iamClient, httpClient, cache, logger, options);
});

// Register the background sync worker
builder.Services.AddHostedService<SyncWorker>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("IAM Edge Gateway starting. Gateway device: {DeviceId}, Cloud IAM: {BaseUrl}",
    gatewayOptions.GatewayDeviceId, gatewayOptions.BaseUrl);

host.Run();
