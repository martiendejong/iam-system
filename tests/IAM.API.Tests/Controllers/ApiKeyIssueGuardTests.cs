using System.Security.Claims;
using Hazina.Security.ApiKeys;
using IAM.API.Controllers;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// JengoWork task 3911: POST /api/api-keys can never mint a key with more power than its caller. Driven straight on
/// the controller (the shared test host pins the default authorization policy to JWT, which hides API-key callers
/// from plain [Authorize] endpoints; the production pipeline accepts them).
/// </summary>
public class ApiKeyIssueGuardTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private sealed class RecordingService : IApiKeyService
    {
        public string? IssuedScope { get; private set; }
        public Guid? IssuedTenant { get; private set; }
        public bool Issued { get; private set; }

        public Task<(ApiKey Key, string RawKey)> CreateApiKeyAsync(string name, Guid? userId, Guid? tenantId, List<string>? permissions = null,
            DateTime? expiresAt = null, int? rateLimitPerMinute = null, string? description = null, string scope = "read", CancellationToken ct = default)
        {
            Issued = true;
            IssuedScope = scope;
            IssuedTenant = tenantId;
            var key = new ApiKey { Name = name, KeyHash = "h", KeyPrefix = "iam_test_", TenantId = tenantId, Scope = scope, UserId = userId };
            return Task.FromResult((key, "iam_test_raw"));
        }

        public Task<List<ApiKey>> GetApiKeysAsync(Guid? userId = null, Guid? tenantId = null, CancellationToken ct = default) => Task.FromResult(new List<ApiKey>());
        public Task<bool> RevokeApiKeyAsync(Guid keyId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<(bool Success, string? NewRawKey)> RotateApiKeyAsync(Guid keyId, CancellationToken ct = default) => Task.FromResult((true, (string?)"x"));
    }

    private static ClaimsPrincipal ApiKeyCaller(ApiKeyScope scope, Guid? tenant) => ApiKeyPrincipal.Create(new ApiKeyRecord
    {
        Id = Guid.NewGuid().ToString("D"),
        KeyHash = "hash",
        KeyPrefix = "iam_test_",
        Name = "caller",
        Scope = scope,
        TenantId = tenant?.ToString("D"),
        UserId = Guid.NewGuid().ToString("D"),
    });

    private static ClaimsPrincipal UserCaller(params string[] roles) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D")) }
            .Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))),
        "test"));

    private static async Task<(IActionResult Result, RecordingService Service)> CreateAsync(
        ClaimsPrincipal caller, string? scope = null, Guid? tenantId = null)
    {
        var service = new RecordingService();
        var controller = new ApiKeysController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = caller } },
        };
        var result = await controller.CreateApiKey(new CreateApiKeyRequest("new-key", tenantId, Scope: scope));
        return (result, service);
    }

    private static void AssertForbidden((IActionResult Result, RecordingService Service) outcome)
    {
        var forbidden = Assert.IsType<ObjectResult>(outcome.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.False(outcome.Service.Issued);
    }

    [Fact]
    public async Task ApiKey_CannotIssueAHigherScopeThanItsOwn()
    {
        AssertForbidden(await CreateAsync(ApiKeyCaller(ApiKeyScope.Write, null), "admin"));
        AssertForbidden(await CreateAsync(ApiKeyCaller(ApiKeyScope.Read, null), "write"));
    }

    [Fact]
    public async Task ApiKey_CanIssueUpToItsOwnScope()
    {
        var (result, service) = await CreateAsync(ApiKeyCaller(ApiKeyScope.Write, null), "read");

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal("read", service.IssuedScope);
    }

    [Fact]
    public async Task TenantKey_OmittingTheTenant_IssuesIntoItsOwnTenant_NeverPlatformWide()
    {
        var (result, service) = await CreateAsync(ApiKeyCaller(ApiKeyScope.Write, TenantA), "write", tenantId: null);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(TenantA, service.IssuedTenant);
    }

    [Fact]
    public async Task TenantKey_CannotIssueIntoAnotherTenant()
    {
        AssertForbidden(await CreateAsync(ApiKeyCaller(ApiKeyScope.Admin, TenantA), "read", TenantB));
    }

    [Fact]
    public async Task PlatformKey_NeedsAdminScopeToIssueIntoATenant()
    {
        AssertForbidden(await CreateAsync(ApiKeyCaller(ApiKeyScope.Write, null), "read", TenantA));

        var (result, service) = await CreateAsync(ApiKeyCaller(ApiKeyScope.Admin, null), "read", TenantA);
        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(TenantA, service.IssuedTenant);
    }

    [Fact]
    public async Task User_NeedsAnAdminRole_ToIssueAnAdminScopeKey()
    {
        AssertForbidden(await CreateAsync(UserCaller("TenantAdmin"), "admin"));

        var (result, service) = await CreateAsync(UserCaller("SystemAdmin"), "admin");
        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal("admin", service.IssuedScope);
    }

    [Fact]
    public async Task User_KeepsIssuingReadAndWriteKeys_AsBefore()
    {
        var (readResult, readService) = await CreateAsync(UserCaller(), scope: null);
        Assert.IsType<CreatedAtActionResult>(readResult);
        Assert.Equal("read", readService.IssuedScope); // default

        var (writeResult, writeService) = await CreateAsync(UserCaller(), "write", TenantA);
        Assert.IsType<CreatedAtActionResult>(writeResult);
        Assert.Equal("write", writeService.IssuedScope);
    }

    [Fact]
    public async Task UnknownScope_IsABadRequest()
    {
        var (result, service) = await CreateAsync(UserCaller("SystemAdmin"), "superuser");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(service.Issued);
    }
}
