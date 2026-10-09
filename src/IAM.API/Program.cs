using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Hazina.Security.ApiKeys;
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
builder.Services.AddScoped<IAM.API.Authorization.IAuditAccessResolver, IAM.API.Authorization.AuditAccessResolver>();
builder.Services.AddScoped<IAM.API.Authorization.ITelemetryAccessAuthorizer, IAM.API.Authorization.TelemetryAccessAuthorizer>();
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
builder.Services.AddIamApiKeyAuth(builder.Configuration, builder.Environment); // shared Hazina.Security.ApiKeys middleware + policies
// Task 5165: the CA key file password must be configured outside Development. Resolved here, at startup and before
// any CA file is read, so a missing Ca:CertificatePassword stops the app (same style as the OpenIddict signing
// certificate check below) instead of falling back to a password that is written in the source.
builder.Services.AddSingleton(CertificateAuthoritySettings.FromConfiguration(builder.Configuration, builder.Environment.IsDevelopment()));
builder.Services.AddScoped<ICertificateAuthorityService, CertificateAuthorityService>();
builder.Services.AddScoped<IEventBus, EventBusService>();
builder.Services.AddScoped<IWebhookService, WebhookService>();
builder.Services.AddSingleton<IHostResolver, DnsHostResolver>();
builder.Services.AddSingleton<IWebhookUrlGuard, WebhookUrlGuard>();
builder.Services.AddScoped<IAM.API.Authorization.IWebhookAccessResolver, IAM.API.Authorization.WebhookAccessResolver>();
builder.Services.AddScoped<IAM.API.Authorization.ITenantAccessResolver, IAM.API.Authorization.TenantAccessResolver>();
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
builder.Services.AddScoped<ILdapDirectoryClient, LdapDirectoryClient>();
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
builder.Services.AddScoped<IAM.API.Authorization.ISecretsAccessResolver, IAM.API.Authorization.SecretsAccessResolver>();
builder.Services.AddScoped<ISecurityAlertService, SecurityAlertService>();
builder.Services.AddScoped<IVisitorService, VisitorService>();
builder.Services.AddScoped<IServiceAccountService, ServiceAccountService>();
builder.Services.AddScoped<IPrincipalDirectoryService, PrincipalDirectoryService>();
builder.Services.AddScoped<IResolverService, ResolverService>();
builder.Services.AddScoped<IRegionService, RegionService>();
builder.Services.AddScoped<IDelegationService, DelegationService>();

// HttpClient for webhook delivery
builder.Services.AddHttpClient("WebhookDelivery", client => {
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(sp =>
    // Task 4702: connection-time SSRF guard, no redirects, no proxy. Self-signed certificates are
    // allowed in development only.
    WebhookHttpHandler.Create(sp.GetRequiredService<IWebhookUrlGuard>(), builder.Environment.IsDevelopment()));

// HttpClient for social/enterprise SSO provider calls
builder.Services.AddHttpClient("SocialAuth");

// HttpClient for Twilio SMS API
builder.Services.AddHttpClient("TwilioSms");

// HttpClient for region health checks (on demand and from RegionHealthWorker). Task 5153: the same connection-time
// SSRF guard as webhook delivery (private, loopback and link-local addresses are never dialled), no redirects, no proxy.
builder.Services.AddHttpClient("RegionHealth", client => {
    client.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(sp =>
    WebhookHttpHandler.Create(sp.GetRequiredService<IWebhookUrlGuard>(), builder.Environment.IsDevelopment()));

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
            "tenants",
            // Task 4099: resource-server scopes (their scope→audience mapping lives in the
            // scope store); registering them here makes discovery advertise them in
            // scopes_supported.
            "taskmanager_api",
            "jengo_mcp"
            // NOTE: "iam_resolver" (task 4059) is intentionally NOT in this list —
            // RegisterScopes populates scopes_supported in the discovery document,
            // and the resolver scope must stay undiscoverable to reduce attack surface.
            // The scope is in the OpenIddict scope store (DatabaseSeeder.SeedScopesAsync)
            // and the token endpoint enforces it via the jengo-vault client's permission list.
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
            var signingCertPath = builder.Configuration["OpenIddict:SigningCertificatePath"];
            var signingCertPass = builder.Configuration["OpenIddict:SigningCertificatePassword"];
            if (string.IsNullOrEmpty(signingCertPath) || !File.Exists(signingCertPath))
            {
                // Fail fast — no production certificate configured
                throw new InvalidOperationException(
                    "Production OpenIddict signing certificate not configured. " +
                    "Set OpenIddict:SigningCertificatePath and OpenIddict:SigningCertificatePassword in configuration.");
            }

            // Dedicated encryption certificate (task 3314, follow-up to #110): AddEphemeralEncryptionKey()
            // regenerates its RSA key in memory on every process start, so every refresh token issued
            // before a restart (deploy, crash, IIS app pool recycle) becomes permanently undecryptable
            // on the next one, regardless of the 7-day refresh token lifetime. A persisted certificate
            // (KeyEncipherment usage — a signing-only cert like signing.pfx is rejected by OpenIddict)
            // fixes that by keeping the same encryption key across restarts.
            var encryptionCertPath = builder.Configuration["OpenIddict:EncryptionCertificatePath"];
            var encryptionCertPass = builder.Configuration["OpenIddict:EncryptionCertificatePassword"];
            if (string.IsNullOrEmpty(encryptionCertPath) || !File.Exists(encryptionCertPath))
            {
                // Fail fast — same pattern as the signing certificate above
                throw new InvalidOperationException(
                    "Production OpenIddict encryption certificate not configured. " +
                    "Set OpenIddict:EncryptionCertificatePath and OpenIddict:EncryptionCertificatePassword in configuration.");
            }

            var signingCert = X509CertificateLoader.LoadPkcs12FromFile(signingCertPath, signingCertPass);
            var encryptionCert = X509CertificateLoader.LoadPkcs12FromFile(encryptionCertPath, encryptionCertPass);
            options.AddSigningCertificate(signingCert).AddEncryptionCertificate(encryptionCert);
        }

        // Register ASP.NET Core host and enable endpoint passthrough
        options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .EnableUserInfoEndpointPassthrough()
               .EnableStatusCodePagesIntegration()
               .DisableTransportSecurityRequirement(); // Allow HTTP when behind IIS/ARR reverse proxy

        // Access tokens are issued as signed-only RS256 JWTs (3 parts), not encrypted JWEs (task 3481).
        // OpenIddict encrypts every token type by default, which made access tokens unreadable to any
        // service without IAM's private encryption key, so resource servers (TaskManager, the MCP
        // validator) could not verify them offline via /.well-known/jwks. Signed-only tokens verify
        // against the published signing key. Claims in an access token are limited to sub, role,
        // tenant_id, scope (see AuthorizationController.GetDestinations); name/email stay id_token-only.
        // Refresh tokens and authorization codes stay encrypted, so the encryption certificate above
        // is still required. Tokens issued before this change remain valid until they expire: the
        // validation handler keeps the encryption key, so it still decrypts the old JWEs.
        options.DisableAccessTokenEncryption();

        // Configure token lifetimes
        options.SetAccessTokenLifetime(TimeSpan.FromMinutes(15))
               .SetRefreshTokenLifetime(TimeSpan.FromDays(7));

        // Reject 'plain' PKCE — code_challenge equals code_verifier so any intercepted
        // authorization request leaks the verifier. All seeded clients use S256.
        // Removing from CodeChallengeMethods also drops 'plain' from the discovery document.
        options.Configure(o => o.CodeChallengeMethods.Remove(OpenIddictConstants.CodeChallengeMethods.Plain));
    })
    .AddValidation(options =>
    {
        // Use local server for token validation
        options.UseLocalServer();

        // Enable ASP.NET Core integration
        options.UseAspNetCore();
    });

// JWT Authentication
// The secret key is read lazily inside the AddJwtBearer options delegate (invoked by the DI
// container on first options resolution, after builder.Build()) rather than as a bare top-level
// statement here — a bare read at this point runs before WebApplicationFactory's test config
// override (ConfigureAppConfiguration, applied during the intercepted Build() call) is visible
// on builder.Configuration, which throws "JWT secret key not configured" for every integration test.
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    var jwtSecretKey = builder.Configuration["Jwt:SecretKey"] ?? throw new InvalidOperationException("JWT secret key not configured");
    var key = Encoding.UTF8.GetBytes(jwtSecretKey);

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

// Task 4059: scope-based authorization requirement for the vault resolver endpoint
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, IAM.API.Auth.ScopeRequirementHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(IAM.API.Controllers.ResolverController.PolicyName, policy =>
    {
        policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new IAM.API.Auth.ScopeRequirement(IAM.API.Controllers.ResolverController.ResolverScope));
    });
});

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
                  "https://maendeleo.martiendejong.nl",
                  "https://knowledge.prospergenics.com")
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
// Token-endpoint brute-force throttle (task 4097). Must run BEFORE UseAuthentication: OpenIddict
// rejects invalid client credentials inside the authentication middleware, so failed attempts never
// reach the general UseRateLimiting below. Keyed on RemoteIpAddress, which UseForwardedHeaders
// (first in the pipeline) has already rewritten from the trusted proxy's X-Forwarded-For.
app.UseTokenEndpointRateLimiting();
app.UseHazinaApiKeyAuth(); // API key auth + per-key rate limiting before JWT (sets HttpContext.User if the X-Api-Key header is present)
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
