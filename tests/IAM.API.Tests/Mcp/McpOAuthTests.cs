using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.API.Mcp.OAuth;
using IAM.API.Tests.Infrastructure;
using IAM.API.Workers;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Tests.Mcp;

/// <summary>
/// The IAM MCP endpoint as an OAuth-protected resource (docs/MCP.md). Everything runs through the real pipeline
/// in-process: dynamic client registration, the consent page, /connect/authorize with PKCE, /connect/token (code and
/// refresh), and then JSON-RPC against /mcp with the access token that came out.
/// </summary>
public class McpOAuthTests : IClassFixture<McpOAuthTests.Factory>
{
    private const string Password = "Str0ngTestPassw0rd!MCP1";
    private const string ChatGptRedirect = "https://chatgpt.com/connector/oauth/abc123";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-signing-key-mcp" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "test-encryption-key-mcp" };

    public sealed class Factory : IAMTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // One test host sees all requests as the same client address; the production throttles are tested elsewhere.
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:TokenEndpointFailureLimitPerMinute"] = "0",
                ["RateLimiting:AnonymousLimitPerMinute"] = "100000",
                ["RateLimiting:AuthenticatedLimitPerMinute"] = "100000",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIddictServerOptions>(options =>
                {
                    options.SigningCredentials.Clear();
                    options.SigningCredentials.Add(new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));
                    options.EncryptionCredentials.Clear();
                    options.EncryptionCredentials.Add(new EncryptingCredentials(
                        EncryptionKey, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
                });
            });
        }
    }

    private readonly Factory _factory;

    public McpOAuthTests(Factory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------------------------------

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    private async Task SeedScopesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await DatabaseSeeder.SeedScopesAsync(scope.ServiceProvider, CancellationToken.None);
    }

    private async Task<(Guid Id, string Email)> SeedUserAsync(params string[] roleNames)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var id = Guid.NewGuid();
        var email = $"mcp-{suffix}@mcp-oauth.test";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.Users.Add(new User
        {
            Id = id, Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = "Mcp", LastName = suffix, EmailConfirmed = true, IsActive = true, TwoFactorEnabled = true,
            TwoFactorSecret = "TOP-SECRET-2FA-SEED"
        });
        foreach (var name in roleNames)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == name);
            if (role == null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = name, IsSystemRole = true };
                db.Roles.Add(role);
            }
            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = id, RoleId = role.Id, GrantedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();
        return (id, email);
    }

    private static async Task LoginAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.True(login.StatusCode == HttpStatusCode.OK, $"Login failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()}");
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> RegisterAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/connect/register", body);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text));
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string AuthorizeQuery(string clientId, string challenge, string redirectUri = ChatGptRedirect, string scope = "mcp offline_access", string state = "st-1") =>
        $"?client_id={Uri.EscapeDataString(clientId)}&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&scope={Uri.EscapeDataString(scope)}&state={state}&code_challenge={challenge}&code_challenge_method=S256";

    private static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     html, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">"))
            fields[System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)] = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
        return fields;
    }

    /// <summary>Registers a client, signs the user in, walks the consent page and returns the token response.</summary>
    private async Task<(string ClientId, JsonElement Tokens, HttpClient Browser)> ConnectAsync(string email, string redirectUri = ChatGptRedirect)
    {
        await SeedScopesAsync();
        var anonymous = NewClient();
        var (status, reg) = await RegisterAsync(anonymous, new { redirect_uris = new[] { redirectUri }, client_name = "ChatGPT", token_endpoint_auth_method = "none" });
        Assert.Equal(HttpStatusCode.Created, status);
        var clientId = reg.GetProperty("client_id").GetString()!;

        var browser = NewClient();
        await LoginAsync(browser, email);
        var (verifier, challenge) = Pkce();

        var consent = await browser.GetAsync("/connect/authorize" + AuthorizeQuery(clientId, challenge, redirectUri));
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        var fields = HiddenFields(await consent.Content.ReadAsStringAsync());
        Assert.True(fields.ContainsKey(McpConsentService.TokenField));

        fields[McpConsentService.DecisionField] = McpConsentService.AllowValue;
        var allowed = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
        var location = allowed.Headers.Location!;
        Assert.StartsWith(redirectUri, location.ToString());
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("st-1", query["state"].ToString());
        var code = query["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code));

        var tokenResponse = await anonymous.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId, ["code_verifier"] = verifier,
        }));
        var body = await tokenResponse.Content.ReadAsStringAsync();
        Assert.True(tokenResponse.StatusCode == HttpStatusCode.OK, $"Token exchange failed: {tokenResponse.StatusCode} {body}");
        return (clientId, JsonSerializer.Deserialize<JsonElement>(body), browser);
    }

    /// <summary>One JSON-RPC call against the stateless streamable-http endpoint; handles JSON and SSE answers.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Result)> RpcAsync(HttpClient client, string? bearer, string method, object? parameters = null, int id = 1)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, default);
        var json = text.TrimStart().StartsWith("{")
            ? text
            : string.Join("", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()).Take(1));
        return (response.StatusCode, JsonSerializer.Deserialize<JsonElement>(json));
    }

    private static string ToolText(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool ToolIsError(JsonElement rpc) =>
        rpc.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean();

    // ----- redirect URI allowlist ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://chatgpt.com/connector/oauth/abc123")]
    [InlineData("https://chatgpt.com/connector/oauth/Ab_9-x")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect")]
    [InlineData("https://platform.openai.com/apps-manage/oauth")]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://claude.com/api/mcp/auth_callback")]
    [InlineData("http://localhost:3334/callback")]
    [InlineData("http://127.0.0.1:8080/oauth/callback")]
    public void RedirectPolicy_AllowsTheKnownConnectorCallbacks(string uri) => Assert.True(McpRedirectUriPolicy.IsAllowed(uri));

    [Theory]
    [InlineData("https://chatgpt.com/connector/oauth/abc/def")]
    [InlineData("https://chatgpt.com/connector/oauth/")]
    [InlineData("https://chatgpt.com/connector/oauth/abc?x=1")]
    [InlineData("https://chatgpt.com.evil.example/connector/oauth/abc")]
    [InlineData("https://evilchatgpt.com/connector/oauth/abc")]
    [InlineData("https://chatgpt.com@evil.example/connector/oauth/abc")]
    [InlineData("http://chatgpt.com/connector/oauth/abc")]
    [InlineData("https://claude.ai/api/mcp/auth_callback/extra")]
    [InlineData("https://claude.ai.evil.example/api/mcp/auth_callback")]
    [InlineData("https://evil.example/callback")]
    [InlineData("http://localhost.evil.example/callback")]
    [InlineData("http://localhost:99999/callback")]
    [InlineData("http://user@localhost:3000/callback")]
    [InlineData("https://claude.ai/api/mcp/auth_callback#frag")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void RedirectPolicy_RejectsEverythingElse(string uri) => Assert.False(McpRedirectUriPolicy.IsAllowed(uri));

    // ----- 401 + discovery ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Mcp_WithoutToken_Answers401WithResourceMetadataChallenge(string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), "/mcp");
        if (method == "POST") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
        Assert.Contains("Bearer", challenge);
        Assert.Contains("resource_metadata=", challenge);
        Assert.Contains("/.well-known/oauth-protected-resource", challenge);
    }

    [Fact]
    public async Task ProtectedResourceMetadata_PointsAtIamAsTheAuthorizationServer()
    {
        var client = NewClient();
        foreach (var url in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("https://localhost:5001/", doc.GetProperty("authorization_servers")[0].GetString());
            var scopes = doc.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()).ToList();
            Assert.Contains("mcp", scopes);
            Assert.DoesNotContain("iam", scopes);
        }
    }

    // OpenIddict itself answers the exact path (its OIDC document, now with registration_endpoint); the issuer-path
    // variants a client builds from an issuer like https://host/auth/ are answered by McpAuthorizationServerMetadataController.
    [Theory]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/oauth-authorization-server/auth/")]
    [InlineData("/.well-known/oauth-authorization-server/auth")]
    public async Task AuthorizationServerMetadata_AdvertisesRegistrationPkceAndPublicClients(string url)
    {
        var response = await NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        if (url.Length > "/.well-known/oauth-authorization-server".Length)
            Assert.Equal("https://localhost:5001/", doc.GetProperty("issuer").GetString());
        Assert.Equal("https://localhost/connect/register", doc.GetProperty("registration_endpoint").GetString());
        Assert.Equal("https://localhost/connect/authorize", doc.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("https://localhost/connect/token", doc.GetProperty("token_endpoint").GetString());
        Assert.Equal(new[] { "S256" }, doc.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Contains("none", doc.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("authorization_code", doc.GetProperty("grant_types_supported").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task OidcDiscovery_NowAlsoCarriesTheRegistrationEndpoint()
    {
        var response = await NewClient().GetAsync("/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("https://localhost/connect/register", doc.GetProperty("registration_endpoint").GetString());
        Assert.Contains("S256", doc.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("mcp", doc.GetProperty("scopes_supported").EnumerateArray().Select(x => x.GetString()));
    }

    // ----- dynamic client registration --------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://chatgpt.com/connector/oauth/abc123")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect")]
    [InlineData("https://platform.openai.com/apps-manage/oauth")]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://claude.com/api/mcp/auth_callback")]
    [InlineData("http://localhost:6274/oauth/callback")]
    [InlineData("http://127.0.0.1:6274/oauth/callback")]
    public async Task Register_AllowedRedirect_Returns201WithAPublicPkceClientHoldingOnlyTheMcpScope(string redirectUri)
    {
        var (status, body) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { redirectUri }, client_name = "ChatGPT", token_endpoint_auth_method = "none" });

        Assert.Equal(HttpStatusCode.Created, status);
        var clientId = body.GetProperty("client_id").GetString()!;
        Assert.StartsWith("mcp-", clientId);
        Assert.Equal("none", body.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("mcp offline_access", body.GetProperty("scope").GetString());
        Assert.False(body.TryGetProperty("client_secret", out _));

        using var scope = _factory.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = (await apps.FindByClientIdAsync(clientId))!;
        Assert.Equal(ClientTypes.Public, await apps.GetClientTypeAsync(app));
        Assert.True(await McpOAuth.IsDynamicClientAsync(apps, app));
        Assert.Contains(Requirements.Features.ProofKeyForCodeExchange, await apps.GetRequirementsAsync(app));
        var permissions = (await apps.GetPermissionsAsync(app)).ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + "mcp", Permissions.Prefixes.Scope + "offline_access",
            },
            permissions);
        Assert.Equal(new[] { redirectUri }, (await apps.GetRedirectUrisAsync(app)).ToArray());
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("https://chatgpt.com/connector/oauth/abc/def")]
    [InlineData("https://chatgpt.com.evil.example/connector/oauth/abc")]
    [InlineData("http://chatgpt.com/connector/oauth/abc")]
    [InlineData("myapp://callback")]
    public async Task Register_ForeignRedirect_Returns400InvalidRedirectUri(string redirectUri)
    {
        var (status, body) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { redirectUri }, client_name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_redirect_uri", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Register_OneBadRedirectAmongGoodOnes_RejectsTheWholeRequest()
    {
        var (status, _) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect, "https://evil.example/cb" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Theory]
    [InlineData("client_credentials")]
    [InlineData("password")]
    [InlineData("urn:ietf:params:oauth:grant-type:device_code")]
    public async Task Register_AskingForAnotherGrantType_IsRejected(string grant)
    {
        var (status, body) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect }, grant_types = new[] { "authorization_code", grant } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_client_metadata", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("openid")]
    [InlineData("mcp roles")]
    [InlineData("taskmanager_api")]
    [InlineData("iam_resolver")]
    public async Task Register_AskingForAWiderScope_IsRejected(string scope)
    {
        var (status, body) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect }, scope });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_client_metadata", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Register_ClientSecretAuthMethodRequested_IsAnsweredWithNone_AndNoSecretIsIssued()
    {
        var (status, body) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect }, token_endpoint_auth_method = "client_secret_post" });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("none", body.GetProperty("token_endpoint_auth_method").GetString());
        Assert.False(body.TryGetProperty("client_secret", out _));
    }

    [Fact]
    public async Task Register_ClientNameIsReducedToPlainText()
    {
        var (status, body) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect }, client_name = "<script>alert(1)</script>Evil\u0007" });
        Assert.Equal(HttpStatusCode.Created, status);
        var name = body.GetProperty("client_name").GetString()!;
        Assert.False(name.Contains('<') || name.Contains('>') || name.Contains('\u0007'), name);
    }

    // ----- the full flow ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FullFlow_ConsentPkceCodeExchange_ThenToolsListAndWhoAmI_ThenRefresh()
    {
        var (userId, email) = await SeedUserAsync("SystemAdmin");
        var (clientId, tokens, _) = await ConnectAsync(email);

        // The token is a signed JWT for the MCP audience, with the narrow scope and nothing else about the user.
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        Assert.True(tokens.TryGetProperty("refresh_token", out var refresh) && !string.IsNullOrEmpty(refresh.GetString()));
        Assert.False(tokens.TryGetProperty("id_token", out _));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        Assert.Contains("iam_mcp", jwt.Audiences);
        Assert.Equal(userId.ToString(), jwt.Subject);
        var scopeClaims = jwt.Claims.Where(c => c.Type == "scope").SelectMany(c => c.Value.Split(' ')).ToList();
        Assert.Contains("mcp", scopeClaims);
        Assert.DoesNotContain("openid", scopeClaims);
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "role" or "roles" or "tenant_id" or "email" or "name");

        var client = NewClient();
        var (initStatus, init) = await RpcAsync(client, accessToken, "initialize", new
        {
            protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" },
        });
        Assert.Equal(HttpStatusCode.OK, initStatus);
        Assert.Equal("Jengo IAM", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        var (listStatus, list) = await RpcAsync(client, accessToken, "tools/list", id: 2);
        Assert.Equal(HttpStatusCode.OK, listStatus);
        var names = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "get_user", "list_audit_events", "list_clients", "list_sessions", "list_users", "whoami" }, names.OrderBy(n => n).ToArray());
        // Every tool is flagged read-only.
        Assert.All(list.GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));

        var (_, whoami) = await RpcAsync(client, accessToken, "tools/call", new { name = "whoami", arguments = new { } }, id: 3);
        Assert.False(ToolIsError(whoami));
        var me = JsonSerializer.Deserialize<JsonElement>(ToolText(whoami));
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.True(me.GetProperty("isIamAdmin").GetBoolean());
        Assert.Equal(clientId, me.GetProperty("connectedClientId").GetString());
        Assert.Contains("SystemAdmin", me.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString()));

        // Refresh: a new access token comes back and works at /mcp.
        var refreshed = await NewClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refresh.GetString()!, ["client_id"] = clientId,
        }));
        var refreshedBody = await refreshed.Content.ReadAsStringAsync();
        Assert.True(refreshed.StatusCode == HttpStatusCode.OK, $"Refresh failed: {refreshed.StatusCode} {refreshedBody}");
        var refreshedJson = JsonSerializer.Deserialize<JsonElement>(refreshedBody);
        var newAccess = refreshedJson.GetProperty("access_token").GetString()!;
        var newJwt = new JwtSecurityTokenHandler().ReadJwtToken(newAccess);
        Assert.Contains("iam_mcp", newJwt.Audiences);
        Assert.DoesNotContain(newJwt.Claims, c => c.Type is "role" or "roles" or "tenant_id" or "email" or "name");
        var (_, again) = await RpcAsync(client, newAccess, "tools/call", new { name = "whoami", arguments = new { } }, id: 4);
        Assert.False(ToolIsError(again));
    }

    [Fact]
    public async Task AdminTools_ReturnUsersClientsSessionsAndAudit_WithoutAnySecret()
    {
        var (adminId, adminEmail) = await SeedUserAsync("SuperAdmin");
        var (_, otherEmail) = await SeedUserAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = adminId, TokenHash = "HASH-THAT-MUST-NOT-LEAK", ExpiresAt = DateTime.UtcNow.AddDays(5), IpAddress = "203.0.113.9", UserAgent = "UnitTest/1.0",
            });
            db.TrustedDevices.Add(new TrustedDevice { UserId = adminId, DeviceFingerprint = "FINGERPRINT-THAT-MUST-NOT-LEAK", Name = "Laptop" });
            db.AuditLogs.Add(new AuditLog { UserId = adminId, Action = "user.login", Resource = "auth", Details = "DETAIL-THAT-MUST-NOT-LEAK", IpAddress = "203.0.113.9" });
            await db.SaveChangesAsync();
        }
        var (_, tokens, _) = await ConnectAsync(adminEmail);
        var token = tokens.GetProperty("access_token").GetString()!;
        var client = NewClient();

        async Task<string> Call(string tool, object args)
        {
            var (_, rpc) = await RpcAsync(client, token, "tools/call", new { name = tool, arguments = args }, id: 9);
            Assert.False(ToolIsError(rpc), ToolText(rpc));
            return ToolText(rpc);
        }

        var users = await Call("list_users", new { search = "mcp-oauth.test", limit = 100 });
        Assert.Contains(adminEmail, users);
        Assert.Contains(otherEmail, users);
        var user = await Call("get_user", new { email = adminEmail });
        Assert.Contains("SuperAdmin", user);
        var clients = await Call("list_clients", new { });
        Assert.Contains("dynamicallyRegistered", clients);
        var sessions = await Call("list_sessions", new { email = adminEmail });
        Assert.Contains("203.0.113.9", sessions);
        Assert.Contains("Laptop", sessions);
        var audit = await Call("list_audit_events", new { action = "user.login" });
        Assert.Contains("user.login", audit);

        foreach (var output in new[] { users, user, clients, sessions, audit })
        {
            foreach (var secret in new[]
                     {
                         "HASH-THAT-MUST-NOT-LEAK", "FINGERPRINT-THAT-MUST-NOT-LEAK", "DETAIL-THAT-MUST-NOT-LEAK", "TOP-SECRET-2FA-SEED",
                         "$2a$", "$2b$", "passwordHash", "PasswordHash", "tokenHash", "clientSecret", "client_secret",
                     })
                Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task NonAdmin_CanOnlySeeThemselves_AdminToolsAreRefused()
    {
        var (userId, email) = await SeedUserAsync();
        var (otherId, _) = await SeedUserAsync();
        var (_, tokens, _) = await ConnectAsync(email);
        var token = tokens.GetProperty("access_token").GetString()!;
        var client = NewClient();

        foreach (var tool in new[] { "list_users", "get_user", "list_clients", "list_audit_events" })
        {
            var (status, rpc) = await RpcAsync(client, token, "tools/call", new { name = tool, arguments = new { email } }, id: 5);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(ToolIsError(rpc), $"{tool} must be refused for a non-admin");
            Assert.StartsWith("Forbidden", ToolText(rpc));
        }

        var (_, me) = await RpcAsync(client, token, "tools/call", new { name = "whoami", arguments = new { } }, id: 6);
        Assert.False(JsonSerializer.Deserialize<JsonElement>(ToolText(me)).GetProperty("isIamAdmin").GetBoolean());

        var (_, own) = await RpcAsync(client, token, "tools/call", new { name = "list_sessions", arguments = new { } }, id: 7);
        Assert.False(ToolIsError(own));

        var (_, foreign) = await RpcAsync(client, token, "tools/call", new { name = "list_sessions", arguments = new { userId = otherId } }, id: 8);
        Assert.True(ToolIsError(foreign));
        Assert.StartsWith("Forbidden", ToolText(foreign));
    }

    [Fact]
    public async Task OAuthCaller_CannotReachTheApiKeyMutatingTools()
    {
        var (_, email) = await SeedUserAsync("SuperAdmin");
        var (_, tokens, _) = await ConnectAsync(email);
        var token = tokens.GetProperty("access_token").GetString()!;

        foreach (var tool in new[] { "send_invitation", "revoke_invitation", "assign_role", "remove_role", "list_roles", "list_tenants", "list_invitations" })
        {
            var response = await CallRawAsync(NewClient(), token, tool);
            // Unknown tool for this principal: a JSON-RPC error, never a result.
            Assert.DoesNotContain("\"result\"", response);
        }
    }

    private static async Task<string> CallRawAsync(HttpClient client, string token, string tool)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = new { } } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await (await client.SendAsync(request)).Content.ReadAsStringAsync();
    }

    // ----- what an MCP token must NOT be, and what must NOT work at /mcp -------------------------------------------------

    [Fact]
    public async Task OrdinaryIamLoginToken_IsNotAcceptedAtMcp()
    {
        var (_, email) = await SeedUserAsync("SuperAdmin");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var clientId = $"normal{suffix}";
        const string redirect = "https://normal.example.test/callback";
        using (var scope = _factory.Services.CreateScope())
        {
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            await apps.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = clientId, ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Implicit, DisplayName = "Normal app",
                RedirectUris = { new Uri(redirect) },
                Permissions =
                {
                    Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.GrantTypes.AuthorizationCode,
                    Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + Scopes.OpenId, Permissions.Prefixes.Scope + Scopes.Roles,
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            });
        }
        var browser = NewClient();
        await LoginAsync(browser, email);
        var (verifier, challenge) = Pkce();
        var authorize = await browser.GetAsync("/connect/authorize" + AuthorizeQuery(clientId, challenge, redirect, "openid roles"));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var tokenResponse = await NewClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["client_id"] = clientId, ["code_verifier"] = verifier,
        }));
        var accessToken = JsonSerializer.Deserialize<JsonElement>(await tokenResponse.Content.ReadAsStringAsync()).GetProperty("access_token").GetString()!;

        var (status, _) = await RpcAsync(NewClient(), accessToken, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Mcp_RejectsGarbageAndForeignJwts()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(client, "not-a-token", "tools/list")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(client, "aaa.bbb.ccc", "tools/list")).Status);
        // A well-formed JWT signed by somebody else.
        var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://localhost/", audience: "iam_mcp",
            claims: new[] { new System.Security.Claims.Claim("sub", Guid.NewGuid().ToString()), new System.Security.Claims.Claim("scope", "mcp") },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)), SecurityAlgorithms.RsaSha256)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(client, forged, "tools/list")).Status);
    }

    [Fact]
    public async Task DeactivatedUser_LosesMcpAccessImmediately()
    {
        var (userId, email) = await SeedUserAsync();
        var (_, tokens, _) = await ConnectAsync(email);
        var token = tokens.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await RpcAsync(NewClient(), token, "tools/list")).Status);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            (await db.Users.FirstAsync(u => u.Id == userId)).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(NewClient(), token, "tools/list")).Status);
    }

    [Fact]
    public async Task RevokingTheAuthorization_EndsMcpAccess()
    {
        var (_, email) = await SeedUserAsync();
        var (clientId, tokens, _) = await ConnectAsync(email);
        var token = tokens.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await RpcAsync(NewClient(), token, "tools/list")).Status);

        using (var scope = _factory.Services.CreateScope())
        {
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var appId = await apps.GetIdAsync((await apps.FindByClientIdAsync(clientId))!);
            await foreach (var authorization in authorizations.FindByApplicationIdAsync(appId!))
                await authorizations.TryRevokeAsync(authorization);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(NewClient(), token, "tools/list")).Status);
    }

    [Fact]
    public async Task KillSwitch_DisablesRegistrationMetadataAndTokens()
    {
        var (_, email) = await SeedUserAsync();
        var (_, tokens, _) = await ConnectAsync(email);
        var token = tokens.GetProperty("access_token").GetString()!;

        using var off = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Mcp:OAuth:Enabled"] = "false" })));
        var client = off.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/connect/register", new { redirect_uris = new[] { ChatGptRedirect } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/.well-known/oauth-authorization-server/auth/")).StatusCode);
        var (status, _) = await RpcAsync(client, token, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    // ----- consent ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConsentPage_StatesPlainlyWhatTheConnectorCanSee_AndEncodesClientControlledText()
    {
        await SeedScopesAsync();
        var (_, email) = await SeedUserAsync();
        var (status, reg) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect }, client_name = "Chat&GPT \"x\"" });
        Assert.Equal(HttpStatusCode.Created, status);
        var browser = NewClient();
        await LoginAsync(browser, email);
        var (_, challenge) = Pkce();

        var page = await browser.GetAsync("/connect/authorize" + AuthorizeQuery(reg.GetProperty("client_id").GetString()!, challenge));
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Contains("read-only", html);
        Assert.Contains("cannot", html);
        Assert.Contains("create, change, delete or invite", html);
        Assert.Contains("never receives passwords", html);
        Assert.Contains("chatgpt.com", html);
        Assert.Contains(email, html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Allow read-only access", html);
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Consent_Denied_RedirectsBackWithAccessDenied_AndIssuesNoCode()
    {
        await SeedScopesAsync();
        var (_, email) = await SeedUserAsync();
        var (_, reg) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect } });
        var browser = NewClient();
        await LoginAsync(browser, email);
        var (_, challenge) = Pkce();
        var page = await browser.GetAsync("/connect/authorize" + AuthorizeQuery(reg.GetProperty("client_id").GetString()!, challenge));
        var fields = HiddenFields(await page.Content.ReadAsStringAsync());
        fields[McpConsentService.DecisionField] = McpConsentService.DenyValue;

        var denied = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        var query = QueryHelpers.ParseQuery(denied.Headers.Location!.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.False(query.ContainsKey("code"));
    }

    [Fact]
    public async Task Consent_ForgedOrReplayedToken_DoesNotIssueACode()
    {
        await SeedScopesAsync();
        var (_, email) = await SeedUserAsync();
        var (_, reg) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect } });
        var clientId = reg.GetProperty("client_id").GetString()!;
        var browser = NewClient();
        await LoginAsync(browser, email);
        var (_, challenge) = Pkce();
        var page = await browser.GetAsync("/connect/authorize" + AuthorizeQuery(clientId, challenge));
        var fields = HiddenFields(await page.Content.ReadAsStringAsync());

        // 1. allow without a token (what a hostile page that cannot read the consent page would post)
        var noToken = new Dictionary<string, string>(fields) { [McpConsentService.DecisionField] = McpConsentService.AllowValue };
        noToken.Remove(McpConsentService.TokenField);
        var r1 = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(noToken));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Contains("Allow read-only access", await r1.Content.ReadAsStringAsync());

        // 2. a made-up token
        var forged = new Dictionary<string, string>(fields) { [McpConsentService.DecisionField] = McpConsentService.AllowValue, [McpConsentService.TokenField] = "CfDJ8-forged" };
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(forged))).StatusCode);

        // 3. a genuine token replayed against a different request (other code_challenge / state)
        var (_, otherChallenge) = Pkce();
        var replay = new Dictionary<string, string>(fields)
        {
            [McpConsentService.DecisionField] = McpConsentService.AllowValue, ["code_challenge"] = otherChallenge, ["state"] = "different",
        };
        var r3 = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(replay));
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);

        // 4. a genuine token carried to another user's session
        var (_, otherEmail) = await SeedUserAsync();
        var otherBrowser = NewClient();
        await LoginAsync(otherBrowser, otherEmail);
        var stolen = new Dictionary<string, string>(fields) { [McpConsentService.DecisionField] = McpConsentService.AllowValue };
        var r4 = await otherBrowser.PostAsync("/connect/authorize", new FormUrlEncodedContent(stolen));
        Assert.Equal(HttpStatusCode.OK, r4.StatusCode);
    }

    [Fact]
    public async Task DynamicClient_AskingForMoreScopesThanItHolds_IsRefusedByTheAuthorizationEndpoint()
    {
        await SeedScopesAsync();
        var (_, email) = await SeedUserAsync("SuperAdmin");
        var (_, reg) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect } });
        var browser = NewClient();
        await LoginAsync(browser, email);
        var (_, challenge) = Pkce();

        foreach (var scope in new[] { "mcp roles", "mcp profile", "taskmanager_api", "mcp iam_resolver", "jengo_mcp", "tenants" })
        {
            var response = await browser.GetAsync("/connect/authorize" + AuthorizeQuery(reg.GetProperty("client_id").GetString()!, challenge, scope: scope));
            // invalid_scope redirect (or a 400): never the consent page and never a code.
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("code=", response.Headers.Location?.ToString() ?? "");
        }
    }

    [Fact]
    public async Task DynamicClient_WithoutPkce_IsRefused()
    {
        await SeedScopesAsync();
        var (_, email) = await SeedUserAsync();
        var (_, reg) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect } });
        var browser = NewClient();
        await LoginAsync(browser, email);

        var response = await browser.GetAsync(
            $"/connect/authorize?client_id={reg.GetProperty("client_id").GetString()}&response_type=code&redirect_uri={Uri.EscapeDataString(ChatGptRedirect)}&scope=mcp&state=s");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("code=", response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task DynamicClient_AskingForOpenId_StillGetsOnlyTheMcpScope_AndNoIdToken()
    {
        await SeedScopesAsync();
        var (_, email) = await SeedUserAsync("SuperAdmin");
        var anonymous = NewClient();
        var (_, reg) = await RegisterAsync(anonymous, new { redirect_uris = new[] { ChatGptRedirect } });
        var clientId = reg.GetProperty("client_id").GetString()!;
        var browser = NewClient();
        await LoginAsync(browser, email);
        var (verifier, challenge) = Pkce();

        var page = await browser.GetAsync("/connect/authorize" + AuthorizeQuery(clientId, challenge, scope: "openid mcp"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var fields = HiddenFields(await page.Content.ReadAsStringAsync());
        fields[McpConsentService.DecisionField] = McpConsentService.AllowValue;
        var allowed = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(fields));
        var code = QueryHelpers.ParseQuery(allowed.Headers.Location!.Query)["code"].ToString();

        var response = await anonymous.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = ChatGptRedirect,
            ["client_id"] = clientId, ["code_verifier"] = verifier,
        }));
        var tokens = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.False(tokens.TryGetProperty("id_token", out _));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.GetProperty("access_token").GetString());
        Assert.DoesNotContain(jwt.Claims.Where(c => c.Type == "scope").SelectMany(c => c.Value.Split(' ')), s => s == "openid");
        Assert.DoesNotContain("iam_api", jwt.Audiences);
    }

    [Fact]
    public async Task DynamicClient_CannotUseTheClientCredentialsOrPasswordGrants()
    {
        await SeedScopesAsync();
        var (_, email) = await SeedUserAsync("SuperAdmin");
        var (_, reg) = await RegisterAsync(NewClient(), new { redirect_uris = new[] { ChatGptRedirect } });
        var clientId = reg.GetProperty("client_id").GetString()!;

        foreach (var form in new[]
                 {
                     new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["scope"] = "mcp" },
                     new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = clientId, ["username"] = email, ["password"] = Password, ["scope"] = "mcp" },
                 })
        {
            var response = await NewClient().PostAsync("/connect/token", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Registration_IsThrottledPerAddress()
    {
        using var limited = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Mcp:OAuth:RegistrationsPerIpPerHour"] = "2" })));
        var client = limited.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/connect/register", new { redirect_uris = new[] { ChatGptRedirect } })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/connect/register", new { redirect_uris = new[] { ChatGptRedirect } })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/connect/register", new { redirect_uris = new[] { ChatGptRedirect } })).StatusCode);
    }
}
