using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Integration tests for DELETE /api/users/{id} (SuperAdmin permanently deletes a user).
/// </summary>
public class UsersControllerDeleteUserTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IAMTestWebApplicationFactory _factory;
    private readonly HttpClient _superAdminClient;

    public UsersControllerDeleteUserTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _superAdminClient = factory.CreateClient();
        _superAdminClient.AddAuthorizationHeader(GenerateSuperAdminToken());
    }

    private static string GenerateSuperAdminToken()
    {
        return TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            "admin@test.com",
            new[] { "SuperAdmin" });
    }

    private async Task<Guid> CreateTargetUserAsync(string email)
    {
        var response = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email,
            password = "Str0ngTestPassw0rd!",
            firstName = "Delete",
            lastName = "Target"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task DeleteUser_AsSuperAdmin_RemovesUserAndReturnsOk()
    {
        // Arrange
        var userId = await CreateTargetUserAsync("delete.me@test.com");

        // Act
        var response = await _superAdminClient.DeleteAsync($"/api/users/{userId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var getResponse = await _superAdminClient.GetAsync($"/api/users/{userId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_UnknownId_ReturnsNotFound()
    {
        // Act
        var response = await _superAdminClient.DeleteAsync($"/api/users/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_OwnAccount_ReturnsBadRequest()
    {
        // Act - the SuperAdmin token's own subject is the seeded admin user id
        var response = await _superAdminClient.DeleteAsync("/api/users/99999999-9999-9999-9999-999999999999");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var getResponse = await _superAdminClient.GetAsync("/api/users/99999999-9999-9999-9999-999999999999");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_AsNonSuperAdmin_ReturnsForbidden()
    {
        // Arrange
        var userId = await CreateTargetUserAsync("forbidden.delete@test.com");
        var userClient = _factory.CreateClient();
        userClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        // Act
        var response = await userClient.DeleteAsync($"/api/users/{userId}");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_Unauthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var userId = await CreateTargetUserAsync("anon.delete@test.com");
        var anonClient = _factory.CreateClient();

        // Act
        var response = await anonClient.DeleteAsync($"/api/users/{userId}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_RemovesRolesTokensRecoveryCodesAndSessions()
    {
        // Arrange
        var userId = await CreateTargetUserAsync("full.cleanup@test.com");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                TenantId = RootTenantId,
                GrantedAt = DateTime.UtcNow
            });

            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = "test-hash",
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            });

            db.RecoveryCodes.Add(new RecoveryCode
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CodeHash = "test-recovery-hash"
            });

            db.UserSessions.Add(new UserSession
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SessionToken = "test-session-token",
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            });

            await db.SaveChangesAsync();
        }

        // Act
        var response = await _superAdminClient.DeleteAsync($"/api/users/{userId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - every related row for this user is gone
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

            Assert.False(await db.UserRoles.AnyAsync(ur => ur.UserId == userId));
            Assert.False(await db.RefreshTokens.AnyAsync(rt => rt.UserId == userId));
            Assert.False(await db.RecoveryCodes.AnyAsync(rc => rc.UserId == userId));
            Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == userId));
        }
    }

    [Fact]
    public async Task DeleteUser_KeepsAuditLogRowsWithUserIdCleared()
    {
        // Arrange
        var userId = await CreateTargetUserAsync("audit.survivor@test.com");
        var auditLogId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

            db.AuditLogs.Add(new AuditLog
            {
                Id = auditLogId,
                UserId = userId,
                TenantId = RootTenantId,
                Action = "Login",
                Resource = "User"
            });

            await db.SaveChangesAsync();
        }

        // Act
        var response = await _superAdminClient.DeleteAsync($"/api/users/{userId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - the audit log row still exists, but its user reference is cleared
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            var auditLog = await db.AuditLogs.FirstOrDefaultAsync(a => a.Id == auditLogId);

            Assert.NotNull(auditLog);
            Assert.Null(auditLog!.UserId);
        }
    }
}
