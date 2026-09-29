using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 3481: IAM access tokens must be verifiable by an external resource server using only the
/// published JWKS. These tests drive the REAL OpenIddict pipeline (login -> /connect/authorize ->
/// /connect/token, and client_credentials) in-process and then validate the issued token the way a
/// resource server would: RS256 only, key from /.well-known/jwks, "at+jwt" type.
///
/// The token-signing/encryption keys are swapped for in-memory RSA keys in the test host. The
/// factory runs in Development, where OpenIddict's AddDevelopment*Certificate() hits a CNG
/// "Keyset does not exist" failure on this orchestration host (see the skip note on
/// AuthorizationEndpointTenantClaimIntegrationTests). Which key material signs/encrypts is
/// irrelevant to what is asserted here - the token FORMAT (signed-only vs encrypted) is set by
/// Program.cs and is not touched by the key swap.
/// </summary>
public class AccessTokenFormatTests
{
    private const string ClientId = "taskmanager";
    private const string RedirectUri = "https://taskmanager.example.test/callback";
    private const string UserEmail = "token-format@test.example";
    private const string UserPassword = "Str0ngTestPassw0rd!3481";
    private static readonly Guid TenantId = Guid.Parse("33333333-3481-3481-3481-333333333333");
    private static readonly Guid UserId = Guid.Parse("44444444-3481-3481-3481-444444444444");

    /// <summary>
    /// Every claim an access token may carry. The token is readable by whoever holds it, so this list is
    /// deliberately closed: adding a claim to the access token must be a conscious edit here AND to
    /// AuthorizationController.GetDestinations, never a side effect. Registered/OpenIddict housekeeping
    /// claims (iss, exp, iat, nbf, jti, client_id, oi_*) plus the three identity claims a resource server
    /// needs: sub, role, tenant_id (and the granted scope).
    /// </summary>
    private static readonly HashSet<string> AllowedAccessTokenClaims = new(StringComparer.Ordinal)
    {
        "iss", "exp", "iat", "nbf", "jti", "aud", "client_id", "scope",
        "sub", "role", "tenant_id",
        "oi_prst", "oi_au_id", "oi_tkn_id"
    };

    private static void AssertOnlyAllowedClaims(string accessToken)
    {
        var unexpected = DecodeSegment(accessToken, 1).EnumerateObject()
            .Select(p => p.Name).Where(n => !AllowedAccessTokenClaims.Contains(n)).ToList();
        Assert.True(unexpected.Count == 0,
            $"access token carries claims outside the allow-list (readable by every holder): {string.Join(", ", unexpected)}");
    }

    // Shared by every factory in this class so a token issued by one host is verifiable by another
    // (that is exactly the situation right after a deploy: same keys, new token format).
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-signing-key" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "test-encryption-key" };

    /// <summary>
    /// The real app, with only the key material replaced. <paramref name="encryptAccessTokens"/> = true
    /// emulates the configuration before task 3481 (OpenIddict's default, encrypted JWE access tokens).
    /// </summary>
    private sealed class TokenFormatFactory : IAMTestWebApplicationFactory
    {
        private readonly bool? _encryptAccessTokens;

        public TokenFormatFactory(bool? encryptAccessTokens = null) => _encryptAccessTokens = encryptAccessTokens;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIddictServerOptions>(options =>
                {
                    options.SigningCredentials.Clear();
                    options.SigningCredentials.Add(new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));
                    options.EncryptionCredentials.Clear();
                    options.EncryptionCredentials.Add(new EncryptingCredentials(
                        EncryptionKey, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));

                    if (_encryptAccessTokens == true)
                        options.DisableAccessTokenEncryption = false;
                });
            });
        }
    }

    // ----- helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// https base address for every client in this class: the IAM.Session cookie is Secure (would not be
    /// sent back over http) and OpenIddict derives the issuer from the request URL when Jwt:Issuer is not
    /// visible to the AddServer callback (test host), so issuing and validating must use the same scheme.
    /// </summary>
    private static HttpClient NewClient(TokenFormatFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    /// <summary>
    /// Task 4099: register scope→audience mappings in the OpenIddict scope store, the way
    /// DatabaseSeeder.SeedScopesAsync does in production (the seeder itself is a hosted service
    /// that the test factory strips out).
    /// </summary>
    private static async Task SeedApiScopesAsync(TokenFormatFactory factory, params (string Scope, string Audience)[] mappings)
    {
        using var scope = factory.Services.CreateScope();
        var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        foreach (var (name, audience) in mappings)
        {
            await scopes.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = name,
                DisplayName = name,
                Resources = { audience }
            });
        }
    }

    private static async Task SeedInteractiveClientAndUserAsync(TokenFormatFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Token Format Tenant", Type = "Organization", IsActive = true });

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "taskmanager:customer",
            Category = $"app:{ClientId}",
            TenantId = null,
            IsSystemRole = false
        };
        db.Roles.Add(role);

        db.Users.Add(new User
        {
            Id = UserId,
            Email = UserEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(UserPassword),
            FirstName = "Token",
            LastName = "Format",
            EmailConfirmed = true,
            IsActive = true
        });

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            RoleId = role.Id,
            TenantId = TenantId,
            GrantedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "TaskManager (token format test client)",
            RedirectUris = { new Uri(RedirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                $"{Permissions.Prefixes.Scope}{Scopes.OpenId}",
                $"{Permissions.Prefixes.Scope}{Scopes.Profile}",
                $"{Permissions.Prefixes.Scope}{Scopes.Email}",
                $"{Permissions.Prefixes.Scope}{Scopes.Roles}",
                $"{Permissions.Prefixes.Scope}{Scopes.OfflineAccess}",
                $"{Permissions.Prefixes.Scope}taskmanager_api" // task 4099 audience tests
            }
        });
    }

    private static async Task SeedServiceClientAsync(TokenFormatFactory factory, string clientId, string secret, params string[] extraScopes)
    {
        using var scope = factory.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Service account (token format test client)",
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                $"{Permissions.Prefixes.Scope}{Scopes.OpenId}"
            }
        };
        foreach (var extraScope in extraScopes)
            descriptor.Permissions.Add($"{Permissions.Prefixes.Scope}{extraScope}");
        await apps.CreateAsync(descriptor);
    }

    /// <summary>Real browser-equivalent flow: login, authorize (cookie), exchange the code.</summary>
    private static async Task<JsonElement> LoginAndGetTokensAsync(TokenFormatFactory factory, string scope = "openid profile email roles offline_access")
    {
        var client = NewClient(factory);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = UserEmail, password = UserPassword });
        Assert.True(login.StatusCode == HttpStatusCode.OK, $"Login failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        var authorizeUrl = "/connect/authorize" +
            $"?client_id={Uri.EscapeDataString(ClientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=" + Uri.EscapeDataString(scope) +
            "&state=xyz";
        var authorize = await client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var location = authorize.Headers.Location!;
        Assert.StartsWith(RedirectUri, location.ToString());
        var code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), $"No authorization code in redirect: {location}");

        var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.StatusCode == HttpStatusCode.OK, $"Token exchange failed: {token.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static async Task<string> GetClientCredentialsTokenAsync(TokenFormatFactory factory, string clientId, string secret, string scope = "openid")
    {
        var client = NewClient(factory);
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = scope
        }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"client_credentials failed: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("access_token").GetString()!;
    }

    private static JsonElement DecodeSegment(string jwt, int index)
    {
        var segment = jwt.Split('.')[index];
        return JsonSerializer.Deserialize<JsonElement>(Base64UrlEncoder.DecodeBytes(segment));
    }

    /// <summary>aud is a string when single-valued and an array when multi-valued (RFC 7519 §4.1.3).</summary>
    private static string[] GetAudiences(JsonElement payload)
    {
        if (!payload.TryGetProperty("aud", out var aud))
            return Array.Empty<string>();
        return aud.ValueKind == JsonValueKind.Array
            ? aud.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : new[] { aud.GetString()! };
    }

    /// <summary>The issuer a resource server would learn from the discovery document.</summary>
    private static async Task<string> GetIssuerAsync(HttpClient client)
    {
        var discovery = JsonSerializer.Deserialize<JsonElement>(await client.GetStringAsync("/.well-known/openid-configuration"));
        return discovery.GetProperty("issuer").GetString()!;
    }

    /// <summary>
    /// What a resource server (TaskManager JwtBearer, MCP validator) would do: fetch the JWKS, accept
    /// only RS256 and only the "at+jwt" token type, check issuer + lifetime. Audience validation is
    /// opt-in per call (task 4099 migration step 2: each resource server turns it on individually,
    /// passing its own audience): with <paramref name="audience"/> = null this validator behaves like
    /// today's consumers, which do not check aud and accept tokens with or without one.
    /// </summary>
    private static async Task<TokenValidationResult> ValidateAsResourceServerAsync(HttpClient client, string token, string? audience = null)
    {
        var jwks = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks"));
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = await GetIssuerAsync(client),
            ValidateIssuer = true,
            ValidateAudience = audience is not null,
            ValidAudience = audience,
            ValidateLifetime = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
            ValidTypes = new[] { "at+jwt" },
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ClockSkew = TimeSpan.Zero
        });
    }

    // ----- tests -----------------------------------------------------------------------------------

    [Fact]
    public async Task ClientCredentialsToken_IsThreePartRs256Jwt_VerifiableAgainstPublishedJwks()
    {
        using var factory = new TokenFormatFactory();
        await SeedServiceClientAsync(factory, "jengo-agi-svc-test", "svc-secret-3481");

        var token = await GetClientCredentialsTokenAsync(factory, "jengo-agi-svc-test", "svc-secret-3481");

        Assert.Equal(3, token.Split('.').Length); // signed JWT, not a 5-part JWE
        var header = DecodeSegment(token, 0);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("at+jwt", header.GetProperty("typ").GetString());
        Assert.False(header.TryGetProperty("enc", out _), "an encrypted token would carry an 'enc' header");

        var client = NewClient(factory);
        var result = await ValidateAsResourceServerAsync(client, token);
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("jengo-agi-svc-test", result.ClaimsIdentity.FindFirst("sub")?.Value);

        AssertOnlyAllowedClaims(token);
        var payload = DecodeSegment(token, 1);
        Assert.Equal("openid", payload.GetProperty("scope").GetString());
        Assert.Equal(TimeSpan.FromMinutes(15),
            TimeSpan.FromSeconds(payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()));
    }

    [Fact]
    public async Task JwksPublishesSigningKeyOnly_NoEncryptionKey()
    {
        using var factory = new TokenFormatFactory();
        var jwks = new JsonWebKeySet(await NewClient(factory).GetStringAsync("/.well-known/jwks"));

        var key = Assert.Single(jwks.Keys);
        Assert.Equal("sig", key.Use);
        Assert.Equal("test-signing-key", key.Kid);
        Assert.False(string.IsNullOrEmpty(key.N)); // public modulus only ...
        Assert.True(string.IsNullOrEmpty(key.D), "the private exponent must never be published");
    }

    [Fact]
    public async Task ResourceServerValidation_RejectsGarbageTamperedUnsignedAndForeignKeyTokens()
    {
        using var factory = new TokenFormatFactory();
        await SeedServiceClientAsync(factory, "jengo-agi-svc-test", "svc-secret-3481");
        var client = NewClient(factory);
        var issuer = await GetIssuerAsync(client);
        var good = await GetClientCredentialsTokenAsync(factory, "jengo-agi-svc-test", "svc-secret-3481");
        Assert.True((await ValidateAsResourceServerAsync(client, good)).IsValid); // control

        // 1. garbage
        Assert.False((await ValidateAsResourceServerAsync(client, "not-a-token")).IsValid);
        Assert.False((await ValidateAsResourceServerAsync(client, "a.b.c")).IsValid);

        // 2. payload tampered (sub swapped), original signature kept
        var parts = good.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64UrlEncoder.DecodeBytes(parts[1]))!;
        payload["sub"] = JsonSerializer.SerializeToElement("someone-else");
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload))}.{parts[2]}";
        Assert.IsAssignableFrom<SecurityTokenInvalidSignatureException>((await ValidateAsResourceServerAsync(client, tampered)).Exception);

        // 3. alg=none, signature stripped
        var noneHeader = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"at+jwt\"}"));
        Assert.False((await ValidateAsResourceServerAsync(client, $"{noneHeader}.{parts[1]}.")).IsValid);

        // 4. well-formed at+jwt signed with a key that is not in the JWKS
        var foreign = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Subject = new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim("sub", "attacker") }),
            Expires = DateTime.UtcNow.AddMinutes(10),
            TokenType = "at+jwt",
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-signing-key" }, SecurityAlgorithms.RsaSha256)
        });
        Assert.IsAssignableFrom<SecurityTokenInvalidSignatureException>((await ValidateAsResourceServerAsync(client, foreign)).Exception);

        // 5. expired
        var expired = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Subject = new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim("sub", "x") }),
            NotBefore = DateTime.UtcNow.AddHours(-2),
            Expires = DateTime.UtcNow.AddHours(-1),
            TokenType = "at+jwt",
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
        });
        Assert.IsAssignableFrom<SecurityTokenExpiredException>((await ValidateAsResourceServerAsync(client, expired)).Exception);
    }

    [Fact]
    public async Task InteractiveLogin_AccessTokenIsSignedJwtWithMinimalClaims_IdTokenAndRefreshTokenAreNot()
    {
        using var factory = new TokenFormatFactory();
        await SeedInteractiveClientAndUserAsync(factory);

        var tokens = await LoginAndGetTokensAsync(factory);
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var idToken = tokens.GetProperty("id_token").GetString()!;
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        // Access token: signed JWT, verifiable via JWKS by a resource server.
        Assert.Equal(3, accessToken.Split('.').Length);
        var result = await ValidateAsResourceServerAsync(NewClient(factory), accessToken);
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(UserId.ToString(), result.ClaimsIdentity.FindFirst("sub")?.Value);

        // Readable, so it must not carry personal data: name and email stay id_token-only.
        AssertOnlyAllowedClaims(accessToken);
        var claims = DecodeSegment(accessToken, 1);
        Assert.Equal("taskmanager:customer", claims.GetProperty("role").GetString());
        Assert.Contains("openid", claims.GetProperty("scope").GetString()!.Split(' '));
        Assert.True(DecodeSegment(idToken, 1).TryGetProperty("email", out _)); // ... it is in the id_token
        Assert.Equal(TenantId.ToString(), claims.GetProperty("tenant_id").GetString());

        // Token confusion guard: the id_token is signed by the same key but must not pass as an access token.
        Assert.Equal(3, idToken.Split('.').Length);
        Assert.NotEqual("at+jwt", DecodeSegment(idToken, 0).GetProperty("typ").GetString());
        Assert.False((await ValidateAsResourceServerAsync(NewClient(factory), idToken)).IsValid,
            "an id_token must be rejected by a resource server that requires typ=at+jwt");

        // Refresh tokens stay encrypted (5-part JWE): unchanged by this task.
        Assert.Equal(5, refreshToken.Split('.').Length);
    }

    [Fact]
    public async Task RefreshGrant_StillWorks_AndIssuesSignedAccessToken()
    {
        using var factory = new TokenFormatFactory();
        await SeedInteractiveClientAndUserAsync(factory);
        var tokens = await LoginAndGetTokensAsync(factory);

        var refreshed = await NewClient(factory).PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = ClientId
        }));
        var body = await refreshed.Content.ReadAsStringAsync();
        Assert.True(refreshed.StatusCode == HttpStatusCode.OK, $"refresh failed: {refreshed.StatusCode} {body}");

        var newAccess = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("access_token").GetString()!;
        Assert.Equal(3, newAccess.Split('.').Length);
        Assert.True((await ValidateAsResourceServerAsync(NewClient(factory), newAccess)).IsValid);
    }

    [Fact]
    public async Task RefreshGrant_ForUserDeactivatedAfterLogin_IsRejected()
    {
        // Access tokens are now readable and verifiable offline by any resource server, so a deactivated
        // account must not be able to keep minting fresh ones from a refresh token issued before it was
        // deactivated (refresh tokens live 7 days).
        using var factory = new TokenFormatFactory();
        await SeedInteractiveClientAndUserAsync(factory);
        var tokens = await LoginAndGetTokensAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            var user = await db.Users.FindAsync(UserId);
            user!.IsActive = false;
            await db.SaveChangesAsync();
        }

        var refreshed = await NewClient(factory).PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = ClientId
        }));
        Assert.True(refreshed.StatusCode == HttpStatusCode.BadRequest || refreshed.StatusCode == HttpStatusCode.Forbidden,
            $"a deactivated user's refresh token was honoured: {refreshed.StatusCode} {await refreshed.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Userinfo_WithClientCredentialsToken_IsRefusedCleanlyNotWithAServerError()
    {
        // A machine token's subject is a client id, not a user GUID.
        using var factory = new TokenFormatFactory();
        await SeedServiceClientAsync(factory, "jengo-agi-svc-test", "svc-secret-3481");
        var token = await GetClientCredentialsTokenAsync(factory, "jengo-agi-svc-test", "svc-secret-3481");

        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await NewClient(factory).SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AccessTokenIssuedBeforeTheSwitch_StillValidatesAtIam_UntilItExpires()
    {
        // "Before": the pre-3481 configuration (OpenIddict default = encrypted JWE access tokens).
        using var before = new TokenFormatFactory(encryptAccessTokens: true);
        await SeedInteractiveClientAndUserAsync(before);
        var oldToken = (await LoginAndGetTokensAsync(before)).GetProperty("access_token").GetString()!;
        Assert.Equal(5, oldToken.Split('.').Length); // proves the emulation: this is what production issues today

        // "After": the real Program.cs configuration, same keys, fresh process (a deploy).
        using var after = new TokenFormatFactory();
        await SeedInteractiveClientAndUserAsync(after);
        var newToken = (await LoginAndGetTokensAsync(after)).GetProperty("access_token").GetString()!;
        Assert.Equal(3, newToken.Split('.').Length);

        // IAM's own validation (userinfo uses the OpenIddict validation scheme) accepts BOTH.
        foreach (var token in new[] { oldToken, newToken })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await NewClient(after).SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"userinfo rejected a {token.Split('.').Length}-part token: {response.StatusCode}");
        }

        // ... and a garbage bearer is still refused.
        var bad = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "garbage");
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient(after).SendAsync(bad)).StatusCode);
    }

    // ----- task 4099: audience claims via scope→resource mapping -----------------------------------

    [Fact]
    public async Task ClientCredentialsToken_CarriesAudienceOfItsTargetService_AndOnlyValidatesThere()
    {
        using var factory = new TokenFormatFactory();
        await SeedApiScopesAsync(factory,
            ("taskmanager_api", "taskmanager_api"),
            ("jengo_mcp", "jengo_mcp"));
        await SeedServiceClientAsync(factory, "jengo-agi-svc-test", "svc-secret-4099", "taskmanager_api");

        var token = await GetClientCredentialsTokenAsync(factory, "jengo-agi-svc-test", "svc-secret-4099",
            scope: "taskmanager_api");

        // The granted scope's mapped resource is emitted as the aud claim.
        Assert.Equal(new[] { "taskmanager_api" }, GetAudiences(DecodeSegment(token, 1)));
        AssertOnlyAllowedClaims(token);

        var client = NewClient(factory);

        // Accepted by the resource server the token was issued for ...
        var atTarget = await ValidateAsResourceServerAsync(client, token, audience: "taskmanager_api");
        Assert.True(atTarget.IsValid, atTarget.Exception?.Message);

        // ... rejected, offline against the same JWKS, by any other resource server that
        // validates aud — the confused-deputy scenario from the task.
        var atOther = await ValidateAsResourceServerAsync(client, token, audience: "jengo_mcp");
        Assert.False(atOther.IsValid);
        Assert.IsAssignableFrom<SecurityTokenInvalidAudienceException>(atOther.Exception);
    }

    [Fact]
    public async Task InteractiveLoginAndRefresh_AccessTokensCarryAudiencesMappedFromGrantedScopes()
    {
        using var factory = new TokenFormatFactory();
        await SeedApiScopesAsync(factory,
            (Scopes.OpenId, "iam_api"),
            ("taskmanager_api", "taskmanager_api"),
            ("jengo_mcp", "jengo_mcp"));
        await SeedInteractiveClientAndUserAsync(factory);

        var tokens = await LoginAndGetTokensAsync(factory,
            scope: "openid profile email roles offline_access taskmanager_api");
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        // One audience per distinct resource mapped from the granted scopes; scopes without a
        // mapping (profile, email, roles here) contribute nothing.
        Assert.Equal(new[] { "iam_api", "taskmanager_api" },
            GetAudiences(DecodeSegment(accessToken, 1)).OrderBy(a => a, StringComparer.Ordinal));
        AssertOnlyAllowedClaims(accessToken);

        var client = NewClient(factory);
        var atTarget = await ValidateAsResourceServerAsync(client, accessToken, audience: "taskmanager_api");
        Assert.True(atTarget.IsValid, atTarget.Exception?.Message);
        Assert.IsAssignableFrom<SecurityTokenInvalidAudienceException>(
            (await ValidateAsResourceServerAsync(client, accessToken, audience: "jengo_mcp")).Exception);

        // The id_token's audience stays the client id — resource mapping only shapes the access token.
        Assert.Equal(new[] { ClientId }, GetAudiences(DecodeSegment(tokens.GetProperty("id_token").GetString()!, 1)));

        // Refresh grant: the fresh access token carries the same audiences (the mapping is
        // re-resolved on every exchange, so it also picks up mapping changes made after login).
        var refreshed = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = ClientId
        }));
        var body = await refreshed.Content.ReadAsStringAsync();
        Assert.True(refreshed.StatusCode == HttpStatusCode.OK, $"refresh failed: {refreshed.StatusCode} {body}");
        var newAccess = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("access_token").GetString()!;

        Assert.Equal(new[] { "iam_api", "taskmanager_api" },
            GetAudiences(DecodeSegment(newAccess, 1)).OrderBy(a => a, StringComparer.Ordinal));
        Assert.True((await ValidateAsResourceServerAsync(client, newAccess, audience: "taskmanager_api")).IsValid);
        Assert.IsAssignableFrom<SecurityTokenInvalidAudienceException>(
            (await ValidateAsResourceServerAsync(client, newAccess, audience: "jengo_mcp")).Exception);
    }

    [Fact]
    public async Task TokenWhoseScopesMapToNoResource_HasNoAudClaim_AndStillValidatesAsToday()
    {
        // Backwards compatibility (migration step 1): no scope store entries at all — the
        // pre-4099 situation for every deployment whose scopes were never mapped. Tokens keep
        // being issued without aud and keep validating at consumers that don't check aud.
        using var factory = new TokenFormatFactory();
        await SeedServiceClientAsync(factory, "jengo-agi-svc-test", "svc-secret-3481");

        var machineToken = await GetClientCredentialsTokenAsync(factory, "jengo-agi-svc-test", "svc-secret-3481");
        Assert.Empty(GetAudiences(DecodeSegment(machineToken, 1)));
        var result = await ValidateAsResourceServerAsync(NewClient(factory), machineToken);
        Assert.True(result.IsValid, result.Exception?.Message);

        await SeedInteractiveClientAndUserAsync(factory);
        var accessToken = (await LoginAndGetTokensAsync(factory)).GetProperty("access_token").GetString()!;
        Assert.Empty(GetAudiences(DecodeSegment(accessToken, 1)));
        Assert.True((await ValidateAsResourceServerAsync(NewClient(factory), accessToken)).IsValid);
    }

    [Fact]
    public async Task SeedScopes_MapsEveryScopeToItsAudience_IsIdempotent_AndOnlyAddsToExistingRows()
    {
        using var factory = new TokenFormatFactory();

        // Pre-existing row, as on every already-deployed database (created by the pre-4099
        // seeder or edited by hand): the seeder may only ADD the mapped audience to it.
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
            await manager.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = "tenants",
                DisplayName = "Tenants (customized)",
                Description = "hand-edited description",
                Resources = { "custom_audience" }
            });
        }

        // Twice: the second run must be a no-op (idempotency).
        for (var run = 0; run < 2; run++)
        {
            using var scope = factory.Services.CreateScope();
            await IAM.API.Workers.DatabaseSeeder.SeedScopesAsync(scope.ServiceProvider, CancellationToken.None);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

            var expected = new Dictionary<string, string>
            {
                [Scopes.OpenId] = "iam_api",
                [Scopes.Profile] = "iam_api",
                [Scopes.Email] = "iam_api",
                [Scopes.Roles] = "iam_api",
                ["tenants"] = "iam_api",
                ["taskmanager_api"] = "taskmanager_api",
                ["jengo_mcp"] = "jengo_mcp"
            };
            foreach (var (name, audience) in expected)
            {
                var entry = await manager.FindByNameAsync(name);
                Assert.NotNull(entry);
                Assert.Contains(audience, await manager.GetResourcesAsync(entry!));
            }

            // Double seeding created no duplicate rows.
            var total = 0;
            await foreach (var _ in manager.ListAsync())
                total++;
            Assert.Equal(expected.Count, total);

            // The customized row kept its data and only gained the mapped audience.
            var tenants = await manager.FindByNameAsync("tenants");
            Assert.Equal("Tenants (customized)", await manager.GetDisplayNameAsync(tenants!));
            var resources = await manager.GetResourcesAsync(tenants!);
            Assert.Contains("custom_audience", resources);
            Assert.Contains("iam_api", resources);
        }
    }
}
