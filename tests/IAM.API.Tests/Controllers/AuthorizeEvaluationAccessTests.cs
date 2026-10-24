using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Hazina.Security.ApiKeys;
using IAM.API.Authorization;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5162: /api/authorize (single, batch, test) and /api/authorize/permissions/{type}/{id} answer a caller about
/// itself; about anyone else only an administrator of the queried tenant or a service account holding the exact
/// authorize:evaluate permission may ask. Callers who only ask about themselves get the verdict, never the evaluation
/// path, matched policy/permission or the reason that names them. Batches with any disallowed item are refused whole.
/// </summary>
public class AuthorizeEvaluationAccessTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private const string PolicyName = "Secret Door Policy 5162";
    private const string RolePermission = "Door:Unlock";

    private readonly IAMTestWebApplicationFactory _factory;

    public AuthorizeEvaluationAccessTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    public static IEnumerable<object[]> Endpoints() =>
        new[] { "single", "batch", "test", "permissions" }.Select(e => new object[] { e });

    public static IEnumerable<object[]> EndpointsAndPrincipalTypes() =>
        from endpoint in new[] { "single", "batch", "test", "permissions" }
        from type in new[] { "user", "device" }
        select new object[] { endpoint, type };

    public static IEnumerable<object[]> TenantAdminRoles() =>
        new[] { "TenantAdmin", "BuildingOwner", "BuildingManager" }.Select(r => new object[] { r });

    // ----- helpers ---------------------------------------------------------------------------

    private async Task<Guid> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@authorize-eval.test", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task GrantRoleAsync(Guid userId, string roleName, Guid? tenantId, string? permissions = null, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        // Role names are global; a role seeded with permissions gets its own unique name so rows never mix.
        var role = permissions == null ? await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName) : null;
        if (role == null)
        {
            role = new Role
            {
                Id = Guid.NewGuid(),
                Name = permissions == null ? roleName : $"{roleName}-{Guid.NewGuid():N}",
                Description = roleName,
                TenantId = tenantId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Permissions = permissions ?? "[]"
            };
            db.Roles.Add(role);
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id,
            TenantId = tenantId,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A user with a role whose permissions list holds Door:Unlock and a named user policy allowing it.</summary>
    private async Task<Guid> CreateUserWithAccessAsync(Guid tenantId)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "Employee", tenantId, $"[\"{RolePermission}\"]");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.Policies.Add(new Policy
        {
            Id = Guid.NewGuid(),
            Name = PolicyName,
            Description = "allows the door",
            TenantId = tenantId,
            UserId = userId,
            Resource = "Door",
            Action = "Unlock",
            Effect = PolicyEffect.Allow,
            Priority = 5,
            IsActive = true,
            CreatedByUserId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task<(Guid Id, string DeviceId, string Permission)> SeedDeviceAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var permission = "acme:hq:floor-1:sensor:1:telemetry:write";
        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceId = $"dev-{Guid.NewGuid():N}",
            Name = "seeded",
            DeviceType = "sensor",
            AuthenticationMethod = "hmac",
            TenantId = tenantId,
            ResourcePath = "acme:hq:floor-1:sensor:1",
            Permissions = $"[\"{permission}\"]",
            IsActive = true
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return (device.Id, device.DeviceId, permission);
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@authorize-eval.test", roles, tenantClaim));

    private async Task<HttpClient> MemberAsync(Guid tenantId, string roleName = "Employee", DateTime? expiresAt = null)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, roleName, tenantId, expiresAt: expiresAt);
        return ClientAs(userId, new[] { roleName });
    }

    private static object Req(string type, Guid principalId, Guid tenantId, string resource = "Door", string action = "Unlock") =>
        new { principalType = type, principalId, tenantId, resource, action };

    private static Task<HttpResponseMessage> AskAsync(
        HttpClient client, string endpoint, string type, Guid principalId, Guid tenantId,
        string resource = "Door", string action = "Unlock") => endpoint switch
    {
        "single" => client.PostAsJsonAsync("/api/authorize", Req(type, principalId, tenantId, resource, action)),
        "batch" => client.PostAsJsonAsync("/api/authorize/batch",
            new { requests = new[] { Req(type, principalId, tenantId, resource, action) } }),
        "test" => client.PostAsJsonAsync("/api/authorize/test", Req(type, principalId, tenantId, resource, action)),
        "permissions" => client.GetAsync($"/api/authorize/permissions/{type}/{principalId}?tenantId={tenantId}"),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null)
    };

    /// <summary>The one decision of a response: the root for single/test, results[0] for a batch.</summary>
    private static async Task<JsonElement> DecisionAsync(HttpResponseMessage response, string endpoint)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return endpoint == "batch" ? json.GetProperty("results")[0] : json;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AssertNoDetail(JsonElement decision)
    {
        Assert.Null(Text(decision, "matchedPolicy"));
        Assert.Null(Text(decision, "matchedPermission"));
        Assert.DoesNotContain(PolicyName, decision.GetRawText());
        if (decision.TryGetProperty("evaluationPath", out var path))
            Assert.Equal(0, path.GetArrayLength());
    }

    // ----- a normal user -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EndpointsAndPrincipalTypes))]
    public async Task NormalUser_AskingAboutAnotherPrincipal_IsForbidden(string endpoint, string type)
    {
        var tenant = await CreateTenantAsync();
        var other = type == "user" ? await CreateUserWithAccessAsync(tenant) : (await SeedDeviceAsync(tenant)).Id;
        var client = await MemberAsync(tenant);

        var response = await AskAsync(client, endpoint, type, other, tenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(PolicyName, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task NormalUser_StockToken_AskingAboutAnotherUser_IsForbidden(string endpoint)
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateUserWithAccessAsync(tenant);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateUserToken());

        var response = await AskAsync(client, endpoint, "user", other, tenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task AdminRoleClaimWithoutTenantMembership_IsForbidden(string endpoint)
    {
        // The role claim is global and forgeable by whoever issued the token; administering a tenant is a UserRoles row.
        var tenant = await CreateTenantAsync();
        var other = await CreateUserWithAccessAsync(tenant);
        var client = ClientAs(await CreateUserAsync(), new[] { "TenantAdmin", "BuildingOwner" });

        var response = await AskAsync(client, endpoint, "user", other, tenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NormalUser_RefusedBeforeInputValidation_SoNothingLeaksAboutIdsOrTypes()
    {
        var tenant = await CreateTenantAsync();
        var client = await MemberAsync(tenant);

        var unknownType = await client.GetAsync($"/api/authorize/permissions/robot/{Guid.NewGuid()}?tenantId={tenant}");
        var emptyId = await client.GetAsync($"/api/authorize/permissions/user/{Guid.Empty}?tenantId={tenant}");

        Assert.Equal(HttpStatusCode.Forbidden, unknownType.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, emptyId.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Anonymous_IsUnauthorized(string endpoint)
    {
        var response = await AskAsync(_factory.CreateClient(), endpoint, "user", Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ----- a caller asking about itself ---------------------------------------------------------

    [Theory]
    [InlineData("single")]
    [InlineData("batch")]
    [InlineData("test")]
    public async Task User_AskingAboutThemselves_GetsTheVerdictWithoutPolicyDetail(string endpoint)
    {
        var tenant = await CreateTenantAsync();
        var userId = await CreateUserWithAccessAsync(tenant);
        var client = ClientAs(userId, new[] { "Employee" });

        var decision = await DecisionAsync(await AskAsync(client, endpoint, "user", userId, tenant), endpoint);

        Assert.True(decision.GetProperty("allowed").GetBoolean());
        Assert.Equal("Access allowed", Text(decision, "reason"));
        AssertNoDetail(decision);
    }

    [Fact]
    public async Task User_AskingAboutThemselves_DeniedVerdictAlsoHasNeutralReason()
    {
        var tenant = await CreateTenantAsync();
        var userId = await CreateUserWithAccessAsync(tenant);
        var client = ClientAs(userId, new[] { "Employee" });

        var decision = await DecisionAsync(
            await AskAsync(client, "single", "user", userId, tenant, "Vault", "Open"), "single");

        Assert.False(decision.GetProperty("allowed").GetBoolean());
        Assert.Equal("Access denied", Text(decision, "reason"));
        AssertNoDetail(decision);
    }

    [Fact]
    public async Task User_ReadingTheirOwnEffectivePermissions_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var userId = await CreateUserWithAccessAsync(tenant);
        var client = ClientAs(userId, new[] { "Employee" });

        var response = await AskAsync(client, "permissions", "user", userId, tenant);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(userId, json.GetProperty("principalId").GetGuid());
        Assert.Contains(json.GetProperty("directPermissions").EnumerateArray(), p => p.GetString() == RolePermission);
        Assert.Contains(json.GetProperty("effectiveAllowed").EnumerateArray(), p => p.GetString() == RolePermission);
    }

    [Fact]
    public async Task User_AskingAboutThemselvesAsDeviceType_IsForbidden()
    {
        // Their own id under the other principal type is not "themselves".
        var tenant = await CreateTenantAsync();
        var userId = await CreateUserAsync();
        var client = ClientAs(userId, new[] { "Employee" });

        var response = await AskAsync(client, "single", "device", userId, tenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Device_AskingAboutItself_GetsTheVerdictWithoutDetail()
    {
        var tenant = await CreateTenantAsync();
        var device = await SeedDeviceAsync(tenant);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(device.Id, device.DeviceId, tenant, device.Permission));

        var decision = await DecisionAsync(
            await AskAsync(client, "test", "device", device.Id, tenant, device.Permission, ""), "test");
        var permissions = await AskAsync(client, "permissions", "device", device.Id, tenant);

        Assert.True(decision.GetProperty("allowed").GetBoolean());
        AssertNoDetail(decision);
        Assert.Equal(HttpStatusCode.OK, permissions.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Device_AskingAboutAnotherDevice_IsForbidden(string endpoint)
    {
        var tenant = await CreateTenantAsync();
        var device = await SeedDeviceAsync(tenant);
        var other = await SeedDeviceAsync(tenant);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(device.Id, device.DeviceId, tenant, device.Permission));

        var response = await AskAsync(client, endpoint, "device", other.Id, tenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- a tenant administrator ----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TenantAdminRoles))]
    public async Task TenantAdmin_AskingAboutAUserOfTheirTenant_GetsTheFullDetail(string adminRole)
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateUserWithAccessAsync(tenant);
        var client = await MemberAsync(tenant, adminRole);

        foreach (var endpoint in new[] { "single", "batch", "test" })
        {
            var decision = await DecisionAsync(await AskAsync(client, endpoint, "user", target, tenant), endpoint);

            Assert.True(decision.GetProperty("allowed").GetBoolean());
            Assert.Equal(PolicyName, Text(decision, "matchedPolicy"));
            Assert.Contains(PolicyName, Text(decision, "reason"));
        }

        var test = await DecisionAsync(await AskAsync(client, "test", "user", target, tenant), "test");
        Assert.True(test.GetProperty("evaluationPath").GetArrayLength() > 0);

        var permissions = await AskAsync(client, "permissions", "user", target, tenant);
        Assert.Equal(HttpStatusCode.OK, permissions.StatusCode);
        var json = await permissions.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(json.GetProperty("directPermissions").EnumerateArray(), p => p.GetString() == RolePermission);
    }

    [Fact]
    public async Task TenantAdmin_AskingAboutADeviceOfTheirTenant_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var device = await SeedDeviceAsync(tenant);
        var client = await MemberAsync(tenant, "TenantAdmin");

        var decision = await DecisionAsync(
            await AskAsync(client, "test", "device", device.Id, tenant, device.Permission, ""), "test");

        Assert.True(decision.GetProperty("allowed").GetBoolean());
        Assert.Equal(device.Permission, Text(decision, "matchedPermission"));
    }

    // The tenant in the question is the caller's own, the device is not: the access check vouches for the tenant it
    // was given, so the effective-permissions lookup itself must tie the device to that tenant.
    [Fact]
    public async Task TenantAdmin_ReadingAForeignDevicesPermissions_ByNamingTheirOwnTenant_GetsNothing()
    {
        var own = await CreateTenantAsync();
        var foreign = await CreateTenantAsync();
        var device = await SeedDeviceAsync(foreign);
        var client = await MemberAsync(own, "TenantAdmin");

        var response = await client.GetAsync($"/api/authorize/permissions/device/{device.Id}?tenantId={own}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(device.Permission, raw);
        var json = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(0, json.GetProperty("directPermissions").GetArrayLength());
        Assert.Equal(0, json.GetProperty("effectiveAllowed").GetArrayLength());
    }

    [Fact]
    public async Task TenantServiceAccount_ReadingAForeignDevicesPermissions_ByNamingItsOwnTenant_GetsNothing()
    {
        var own = await CreateTenantAsync();
        var foreign = await CreateTenantAsync();
        var device = await SeedDeviceAsync(foreign);
        var client = ClientWithToken(
            TestAuthenticationHelper.GenerateTenantServiceAccountToken("tenant-evaluator", own.ToString(), "authorize:evaluate"));

        var response = await client.GetAsync($"/api/authorize/permissions/device/{device.Id}?tenantId={own}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(device.Permission, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TenantAdmin_ReadingTheEffectivePermissionsOfADeviceOfTheirTenant_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var device = await SeedDeviceAsync(tenant);
        var client = await MemberAsync(tenant, "TenantAdmin");

        var response = await client.GetAsync($"/api/authorize/permissions/device/{device.Id}?tenantId={tenant}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(device.Permission, json.GetProperty("effectiveAllowed")[0].GetString());
    }

    [Theory]
    [MemberData(nameof(EndpointsAndPrincipalTypes))]
    public async Task TenantAdmin_AskingAboutAnotherTenant_IsForbidden(string endpoint, string type)
    {
        var own = await CreateTenantAsync();
        var foreign = await CreateTenantAsync();
        var other = type == "user" ? await CreateUserWithAccessAsync(foreign) : (await SeedDeviceAsync(foreign)).Id;
        var client = await MemberAsync(own, "TenantAdmin");

        var response = await AskAsync(client, endpoint, type, other, foreign);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task TenantAdmin_WhoseRoleExpired_IsForbidden(string endpoint)
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateUserWithAccessAsync(tenant);
        var client = await MemberAsync(tenant, "TenantAdmin", expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var response = await AskAsync(client, endpoint, "user", target, tenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task TenantAdmin_WithTokenPinnedToAnotherTenant_IsForbidden(string endpoint)
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateUserWithAccessAsync(tenant);
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", tenant);
        var client = ClientAs(userId, new[] { "TenantAdmin" }, tenantClaim: Guid.NewGuid().ToString());

        var response = await AskAsync(client, endpoint, "user", target, tenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task SuperAdmin_AskingAboutAnyTenant_Succeeds(string endpoint)
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateUserWithAccessAsync(tenant);
        var client = ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });

        var response = await AskAsync(client, endpoint, "user", target, tenant);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ----- batches -----------------------------------------------------------------------------

    [Fact]
    public async Task Batch_WithOneForeignItem_IsRefusedWhole_ForANormalUser()
    {
        var tenant = await CreateTenantAsync();
        var userId = await CreateUserWithAccessAsync(tenant);
        var other = await CreateUserWithAccessAsync(tenant);
        var client = ClientAs(userId, new[] { "Employee" });

        var response = await client.PostAsJsonAsync("/api/authorize/batch", new
        {
            requests = new[] { Req("user", userId, tenant), Req("user", other, tenant) }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("results", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Batch_WithOneItemInAnotherTenant_IsRefusedWhole_ForATenantAdmin()
    {
        var own = await CreateTenantAsync();
        var foreign = await CreateTenantAsync();
        var inOwn = await CreateUserWithAccessAsync(own);
        var inForeign = await CreateUserWithAccessAsync(foreign);
        var client = await MemberAsync(own, "TenantAdmin");

        var response = await client.PostAsJsonAsync("/api/authorize/batch", new
        {
            requests = new[] { Req("user", inOwn, own), Req("user", inForeign, foreign) }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Batch_OfOnlyOwnItems_ForANormalUser_ReturnsEveryDecisionWithoutDetail()
    {
        var tenant = await CreateTenantAsync();
        var userId = await CreateUserWithAccessAsync(tenant);
        var client = ClientAs(userId, new[] { "Employee" });

        var response = await client.PostAsJsonAsync("/api/authorize/batch", new
        {
            requests = new[] { Req("user", userId, tenant), Req("USER", userId, tenant, "Vault", "Open") }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("results").GetArrayLength());
        foreach (var result in json.GetProperty("results").EnumerateArray())
            AssertNoDetail(result);
    }

    [Fact]
    public async Task Batch_OfOwnTenantItems_ForATenantAdmin_KeepsTheDetail()
    {
        var tenant = await CreateTenantAsync();
        var first = await CreateUserWithAccessAsync(tenant);
        var second = await CreateUserWithAccessAsync(tenant);
        var client = await MemberAsync(tenant, "BuildingManager");

        var response = await client.PostAsJsonAsync("/api/authorize/batch", new
        {
            requests = new[] { Req("user", first, tenant), Req("user", second, tenant) }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(json.GetProperty("results").EnumerateArray(), r => Assert.Equal(PolicyName, Text(r, "matchedPolicy")));
    }

    [Fact]
    public async Task Batch_WithANullItem_IsABadRequest_NotAServerError()
    {
        var client = await MemberAsync(await CreateTenantAsync());

        var response = await client.PostAsync("/api/authorize/batch",
            JsonContent.Create(JsonDocument.Parse("{\"requests\":[null]}").RootElement));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ----- service accounts --------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ServiceAccount_WithAuthorizeEvaluate_AsksAboutAnyTenantWithDetail(string endpoint)
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateUserWithAccessAsync(tenant);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("evaluator", "authorize:evaluate"));

        var response = await AskAsync(client, endpoint, "user", target, tenant);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (endpoint != "permissions")
            Assert.Equal(PolicyName, Text(await DecisionAsync(response, endpoint), "matchedPolicy"));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ServiceAccount_WithoutTheExplicitPermission_IsForbidden(string endpoint)
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateUserWithAccessAsync(tenant);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("provisioner", "users:create", "invitations:send"));

        var response = await AskAsync(client, endpoint, "user", target, tenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("authorize:*")]
    [InlineData("authorize")]
    [InlineData("Authorize:Evaluate")]
    [InlineData("authorize:evaluate:all")]
    public async Task ServiceAccount_WithAWildcardOrLookAlikePermission_IsForbidden(string permission)
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateUserWithAccessAsync(tenant);
        var client = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("sloppy", permission));

        foreach (var endpoint in new[] { "single", "batch", "test", "permissions" })
        {
            var response = await AskAsync(client, endpoint, "user", target, tenant);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ServiceAccount_OfATenant_AsksOnlyAboutThatTenant(string endpoint)
    {
        var own = await CreateTenantAsync();
        var foreign = await CreateTenantAsync();
        var inOwn = await CreateUserWithAccessAsync(own);
        var inForeign = await CreateUserWithAccessAsync(foreign);
        var client = ClientWithToken(
            TestAuthenticationHelper.GenerateTenantServiceAccountToken("tenant-evaluator", own.ToString(), "authorize:evaluate"));

        var ownResponse = await AskAsync(client, endpoint, "user", inOwn, own);
        var foreignResponse = await AskAsync(client, endpoint, "user", inForeign, foreign);

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ServiceAccount_WithAnUnreadableTenantClaim_IsForbidden(string endpoint)
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateUserWithAccessAsync(tenant);
        var client = ClientWithToken(
            TestAuthenticationHelper.GenerateTenantServiceAccountToken("broken", "not-a-guid", "authorize:evaluate"));

        var response = await AskAsync(client, endpoint, "user", target, tenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- the resolver itself: API keys, token exchange ----------------------------------------

    private static ClaimsPrincipal ApiKeyCaller(ApiKeyScope scope, Guid? tenant, Guid? issuedBy = null) =>
        ApiKeyPrincipal.Create(new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("D"),
            KeyHash = "hash",
            KeyPrefix = "iam_test_",
            Name = "caller",
            Scope = scope,
            TenantId = tenant?.ToString("D"),
            UserId = (issuedBy ?? Guid.NewGuid()).ToString("D"),
        });

    private static async Task<EvaluationAccess> ResolveAsync(ClaimsPrincipal caller, string type, Guid principalId, Guid tenantId)
    {
        using var db = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var resolver = new EvaluationAccessResolver(new TenantAccessResolver(db));
        var evaluationCaller = await resolver.ResolveAsync(caller, CancellationToken.None);
        return evaluationCaller.Check(type, principalId, tenantId);
    }

    [Fact]
    public async Task ApiKey_WithWriteScopeOnATenant_AsksAboutThatTenantOnly()
    {
        var tenant = Guid.NewGuid();

        var own = await ResolveAsync(ApiKeyCaller(ApiKeyScope.Write, tenant), "user", Guid.NewGuid(), tenant);
        var foreign = await ResolveAsync(ApiKeyCaller(ApiKeyScope.Write, tenant), "user", Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(EvaluationAccess.Admin, own);
        Assert.Equal(EvaluationAccess.Denied, foreign);
    }

    [Fact]
    public async Task ApiKey_WithReadScope_IsNotAnAdmin()
    {
        var tenant = Guid.NewGuid();

        var access = await ResolveAsync(ApiKeyCaller(ApiKeyScope.Read, tenant), "user", Guid.NewGuid(), tenant);

        Assert.Equal(EvaluationAccess.Denied, access);
    }

    [Fact]
    public async Task ApiKey_IssuedByAUser_DoesNotCountAsThatUserAskingAboutThemselves()
    {
        var tenant = Guid.NewGuid();
        var issuer = Guid.NewGuid();

        var access = await ResolveAsync(ApiKeyCaller(ApiKeyScope.Read, tenant, issuer), "user", issuer, tenant);

        Assert.Equal(EvaluationAccess.Denied, access);
    }

    [Fact]
    public async Task PlatformApiKey_NeedsAdminScope()
    {
        var tenant = Guid.NewGuid();

        var admin = await ResolveAsync(ApiKeyCaller(ApiKeyScope.Admin, null), "user", Guid.NewGuid(), tenant);
        var write = await ResolveAsync(ApiKeyCaller(ApiKeyScope.Write, null), "user", Guid.NewGuid(), tenant);

        Assert.Equal(EvaluationAccess.Admin, admin);
        Assert.Equal(EvaluationAccess.Denied, write);
    }

    [Fact]
    public async Task TokenExchangeToken_HasNoSelf()
    {
        var subject = Guid.NewGuid();
        var caller = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, subject.ToString()),
            new Claim("token_type", "token_exchange"),
            new Claim("act_as", subject.ToString())
        }, "test"));

        var access = await ResolveAsync(caller, "user", subject, Guid.NewGuid());

        Assert.Equal(EvaluationAccess.Denied, access);
    }
}
