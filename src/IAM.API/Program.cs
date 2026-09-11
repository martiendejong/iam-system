using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IAM.API.Auth;
using IAM.API.Middleware;
using IAM.API.Workers;
using IAM.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using StackExchange.Redis;

[assembly: InternalsVisibleTo("IAM.API.Tests")]

var builder = WebApplication.CreateBuilder(args);

// Enable Windows service lifecycle (handles STOP signals from SCM properly and sets correct content root)
builder.Host.UseWindowsService();

// Trust the IIS/ARR reverse proxy so X-Forwarded-For reaches the rate limiter
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust only loopback (IIS/ARR running on same machine)
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);      // 127.0.0.1
    options.KnownProxies.Add(IPAddress.IPv6Loopback);  // ::1
    // If there are known external proxy IPs, add them here from config:
    var proxyIps = builder.Configuration.GetSection("ForwardedHeaders:TrustedProxies").Get<string[]>() ?? [];
    foreach (var ip in proxyIps)
        if (IPAddress.TryParse(ip, out var addr))
            options.KnownProxies.Add(addr);
});

// Add services to the container
builder.Services.AddOpenApi();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<IAMDbContext>(options =>
    options.UseNpgsql(connectionString));

// Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IPolicyInheritanceEngine, PolicyInheritanceEngine>();
builder.Services.AddScoped<ITemporalPolicyEngine, TemporalPolicyEngine>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IPolicyTestingService, PolicyTestingService>();
builder.Services.AddScoped<IEmergencyOverrideService, EmergencyOverrideService>();
builder.Services.AddScoped<IPasskeyService, PasskeyService>();
builder.Services.AddScoped<IDeviceService, DeviceService>();
builder.Services.AddScoped<IDeviceAuthenticationService, DeviceAuthenticationService>();
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<IAM.Core.Configuration.AccessMatrixOptions>(builder.Configuration.GetSection(IAM.Core.Configuration.AccessMatrixOptions.SectionName));
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ITotpService, TotpService>();
builder.Services.AddScoped<IGroupService, GroupService>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddSingleton<IConditionEvaluator, ConditionEvaluator>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddScoped<ICertificateAuthorityService, CertificateAuthorityService>();
builder.Services.AddScoped<IEventBus, EventBusService>();
builder.Services.AddScoped<IWebhookService, WebhookService>();
builder.Services.AddScoped<IMqttAuthService, MqttAuthService>();
builder.Services.AddScoped<IUnifiedAuthorizationService, UnifiedAuthorizationService>();
builder.Services.AddScoped<ITelemetryStorageService, TelemetryStorageService>();
builder.Services.AddScoped<ISocialAuthService, SocialAuthService>();
builder.Services.Configure<SmsSettings>(builder.Configuration.GetSection("Sms"));
builder.Services.AddScoped<IMagicLinkService, MagicLinkService>();
builder.Services.AddScoped<ISmsService, SmsService>();
builder.Services.AddScoped<IOtpService, OtpService>();
builder.Services.AddScoped<IConsentService, ConsentService>();
builder.Services.AddScoped<IDataRequestService, DataRequestService>();
builder.Services.AddScoped<IAccountLinkingService, AccountLinkingService>();
builder.Services.AddScoped<IInvitationService, InvitationService>();
builder.Services.AddScoped<IDirectorySyncService, DirectorySyncService>();
builder.Services.AddScoped<IAccessRequestService, AccessRequestService>();
builder.Services.AddScoped<IScimService, ScimService>();
builder.Services.AddScoped<ITenantBrandingService, TenantBrandingService>();
builder.Services.AddScoped<IClaimsMappingService, ClaimsMappingService>();
builder.Services.AddScoped<INetworkPolicyService, NetworkPolicyService>();
builder.Services.AddScoped<IRiskAssessmentService, RiskAssessmentService>();
builder.Services.AddScoped<IPrivilegedAccessService, PrivilegedAccessService>();
builder.Services.AddScoped<IBulkOperationService, BulkOperationService>();
builder.Services.AddScoped<ISecretsVaultService, SecretsVaultService>();
builder.Services.AddScoped<ISecurityAlertService, SecurityAlertService>();
builder.Services.AddScoped<IVisitorService, VisitorService>();
builder.Services.AddScoped<IServiceAccountService, ServiceAccountService>();
builder.Services.AddScoped<IRegionService, RegionService>();
builder.Services.AddScoped<IDelegationService, DelegationService>();

// HttpClient for webhook delivery
builder.Services.AddHttpClient("WebhookDelivery", client => {
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    if (builder.Environment.IsDevelopment())
        // Allow self-signed certificates in development for webhook endpoints
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    return handler;
});

// HttpClient for social/enterprise SSO provider calls
builder.Services.AddHttpClient("SocialAuth");

// HttpClient for Twilio SMS API
builder.Services.AddHttpClient("TwilioSms");

// HttpClient for region health checks
builder.Services.AddHttpClient("RegionHealth")
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new HttpClientHandler();
        if (builder.Environment.IsDevelopment())
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return handler;
    });

// Building Management System services
builder.Services.AddScoped<ILocationService, LocationService>();
builder.Services.AddScoped<IBuildingService, BuildingService>();
builder.Services.AddScoped<IFloorService, FloorService>();
builder.Services.AddScoped<IRoomService, RoomService>();
builder.Services.AddScoped<IRoomGroupService, RoomGroupService>();
builder.Services.AddScoped<IIoTDeviceService, IoTDeviceService>();
builder.Services.AddScoped<IResourcePermissionService, ResourcePermissionService>();

// Memory cache for policy evaluation
builder.Services.AddMemoryCache();

// Fido2 (WebAuthn) configuration
builder.Services.AddFido2(options =>
{
    options.ServerDomain = builder.Configuration["Fido2:ServerDomain"] ?? "localhost";
    options.ServerName = "IAM System";
    options.Origins = builder.Configuration.GetSection("Fido2:Origins").Get<HashSet<string>>()
        ?? new HashSet<string> { "https://localhost:5161" };
    options.TimestampDriftTolerance = builder.Configuration.GetValue<int>("Fido2:TimestampDriftTolerance", 300000);
});

// Hosted services (database seeders)
builder.Services.AddHostedService<DatabaseSeeder>();

// Background job processors
builder.Services.AddHostedService<ExpiredGrantCleanupWorker>();
builder.Services.AddHostedService<CertificateExpiryMonitorWorker>();
builder.Services.AddHostedService<DeviceHeartbeatMonitorWorker>();
builder.Services.AddHostedService<AuditLogCleanupWorker>();
builder.Services.AddHostedService<SessionCleanupWorker>();
builder.Services.AddHostedService<DirectorySyncWorker>();
builder.Services.AddHostedService<AccessRequestExpiryWorker>();
builder.Services.AddHostedService<PamDeescalationWorker>();
builder.Services.AddHostedService<SecretRotationWorker>();
builder.Services.AddHostedService<SecurityAlertWorker>();
builder.Services.AddHostedService<RegionHealthWorker>();

// OpenIddict (OAuth2/OIDC Server)
builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
            .UseDbContext<IAMDbContext>();
    })
    .AddServer(options =>
    {
        // Set issuer from config for discovery document.
        // ARR strips the /auth/ prefix before forwarding to Kestrel, so endpoint URIs must be
        // relative paths (what Kestrel sees) — OpenIddict matches against the stripped path.
        var issuerUri = builder.Configuration["Jwt:Issuer"];
        if (!string.IsNullOrEmpty(issuerUri))
        {
            options.SetIssuer(new Uri(issuerUri.TrimEnd('/') + "/"));
        }
        options.SetAuthorizationEndpointUris("/connect/authorize")
               .SetTokenEndpointUris("/connect/token")
               .SetIntrospectionEndpointUris("/connect/introspect")
               .SetRevocationEndpointUris("/connect/revoke");

        // Enable authorization code flow, refresh token, and client credentials flows.
        // PKCE is enforced per-client via Requirements.Features.ProofKeyForCodeExchange in the seeder —
        // not globally, since confidential clients (e.g. open-webui) don't require it.
        options.AllowAuthorizationCodeFlow()
               .AllowRefreshTokenFlow()
               .AllowClientCredentialsFlow();

        // Register scopes (permissions that clients can request)
        options.RegisterScopes(
            OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Roles,
            "tenants"
        );

        // Register signing and encryption credentials
        if (builder.Environment.IsDevelopment())
        {
            options.AddDevelopmentEncryptionCertificate()
                   .AddDevelopmentSigningCertificate();
        }
        else
        {
            // Production: load from config
            var certPath = builder.Configuration["OpenIddict:SigningCertificatePath"];
            var certPass = builder.Configuration["OpenIddict:SigningCertificatePassword"];
            if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
            {
                var cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPass);
                options.AddSigningCertificate(cert).AddEphemeralEncryptionKey();
            }
            else
            {
                // Fail fast — no production certificate configured
                throw new InvalidOperationException(
                    "Production OpenIddict signing certificate not configured. " +
                    "Set OpenIddict:SigningCertificatePath and OpenIddict:SigningCertificatePassword in configuration.");
            }
        }

        // Register ASP.NET Core host and enable endpoint passthrough
        options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .EnableUserInfoEndpointPassthrough()
               .EnableStatusCodePagesIntegration()
               .DisableTransportSecurityRequirement(); // Allow HTTP when behind IIS/ARR reverse proxy

        // Configure token lifetimes
        options.SetAccessTokenLifetime(TimeSpan.FromMinutes(15))
               .SetRefreshTokenLifetime(TimeSpan.FromDays(7));
    })
    .AddValidation(options =>
    {
        // Use local server for token validation
        options.UseLocalServer();

        // Enable ASP.NET Core integration
        options.UseAspNetCore();
    });

// JWT Authentication
var jwtSecretKey = builder.Configuration["Jwt:SecretKey"] ?? throw new InvalidOperationException("JWT secret key not configured");
var key = Encoding.UTF8.GetBytes(jwtSecretKey);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ClockSkew = TimeSpan.Zero,
        // Configure claim mappings for roles and names
        RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        NameClaimType = System.Security.Claims.ClaimTypes.Name
    };
})
.AddCookie("IAM.Session", options =>
{
    options.Cookie.Name = "IAM.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
});

// SuperAdmin inherits all admin roles (so role-guarded endpoints accept SuperAdmin)
builder.Services.AddScoped<IClaimsTransformation, SuperAdminClaimsTransformation>();

// Redis (for caching) with in-memory fallback
var redisConnectionString = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrEmpty(redisConnectionString))
{
    try
    {
        var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
        redisOptions.AbortOnConnectFail = false; // Allow startup even if Redis is down
        redisOptions.ConnectTimeout = 5000;
        redisOptions.SyncTimeout = 3000;

        var redis = ConnectionMultiplexer.Connect(redisOptions);
        builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        builder.Services.AddSingleton<ICacheService, RedisCacheService>();

        Console.WriteLine("Redis cache service registered successfully");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Redis connection failed: {ex.Message}. Falling back to in-memory cache");
        builder.Services.AddSingleton<ICacheService, MemoryCacheService>();
    }
}
else
{
    Console.WriteLine("Redis not configured. Using in-memory cache");
    builder.Services.AddSingleton<ICacheService, MemoryCacheService>();
}

// SignalR (for real-time telemetry streaming)
builder.Services.AddSignalR();

// CORS (for React admin UI)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                  "http://localhost:5173",
                  "https://localhost:5173",
                  "https://maendeleo.martiendejong.nl")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders(); // Must be first — rewrites RemoteIpAddress from X-Forwarded-For
app.UseHttpsRedirection();
app.UseCors();

// HTML responses (including SPA fallback) must never be cached; hashed assets can be cached indefinitely
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        if (ctx.Response.ContentType?.StartsWith("text/html") == true)
        {
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            ctx.Response.Headers["Pragma"] = "no-cache";
        }
        return Task.CompletedTask;
    });
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name != "index.html")
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
    }
});
app.UseApiKeyAuthentication(); // API key auth before JWT (sets HttpContext.User if X-API-Key header present)
app.UseAuthentication();
app.UseRateLimiting(); // Rate limiting after auth (so we can identify the caller)
app.UseAuthorization();

app.MapControllers();

// SignalR hubs
app.MapHub<IAM.API.Hubs.TelemetryHub>("/hubs/telemetry");

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Unmatched API routes must return JSON 404, never the SPA HTML — otherwise the
// frontend tries to parse index.html as JSON and crashes (e.g. .map is not a function).
app.MapFallback("/api/{**rest}", () => Results.NotFound(new { error = "API endpoint not found" }));

// SPA fallback — serves index.html for any non-API path not matched by a route
app.MapFallbackToFile("index.html");

// Seed development data (only in Development environment)
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
    var seeder = new DevelopmentDataSeeder(context, isDevelopment: true);
    await seeder.SeedAsync();
}

app.Run();

// Make Program class accessible to test projects
public partial class Program { }
