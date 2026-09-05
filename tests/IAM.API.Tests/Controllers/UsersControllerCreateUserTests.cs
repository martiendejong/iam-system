using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Integration tests for POST /api/users (SuperAdmin creates an active user
/// with a manually-set password, no invitation email).
/// </summary>
public class UsersControllerCreateUserTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;
    private readonly HttpClient _superAdminClient;

    public UsersControllerCreateUserTests(IAMTestWebApplicationFactory factory)
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

    [Fact]
    public async Task CreateUser_AsSuperAdmin_ReturnsCreatedActiveUser()
    {
        // Act
        var response = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email = "created.by.admin@test.com",
            password = "Str0ngTestPassw0rd!",
            firstName = "Created",
            lastName = "ByAdmin"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("created.by.admin@test.com", body.GetProperty("email").GetString());
        Assert.Equal("Created", body.GetProperty("firstName").GetString());
        Assert.Equal("ByAdmin", body.GetProperty("lastName").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.True(body.GetProperty("emailConfirmed").GetBoolean());
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task CreateUser_ResponseContainsNoPasswordOrHash()
    {
        // Act
        var response = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email = "no.secret.echo@test.com",
            password = "S3cretTestPassw0rd!",
            firstName = "No",
            lastName = "Echo"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("S3cretTestPassw0rd!", raw);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateUser_AsNonSuperAdmin_ReturnsForbidden()
    {
        // Arrange
        var userClient = _factory.CreateClient();
        userClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        // Act
        var response = await userClient.PostAsJsonAsync("/api/users", new
        {
            email = "forbidden.attempt@test.com",
            password = "Str0ngTestPassw0rd!",
            firstName = "Should",
            lastName = "Fail"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_Unauthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var anonClient = _factory.CreateClient();

        // Act
        var response = await anonClient.PostAsJsonAsync("/api/users", new
        {
            email = "anonymous.attempt@test.com",
            password = "Str0ngTestPassw0rd!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_ShortPassword_ReturnsBadRequest()
    {
        // Act
        var response = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email = "short.password@test.com",
            password = "short7!",
            firstName = "Short",
            lastName = "Password"
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_MissingEmail_ReturnsBadRequest()
    {
        // Act
        var response = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email = "   ",
            password = "Str0ngTestPassw0rd!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_DuplicateEmail_ReturnsConflict()
    {
        // Arrange - user@test.com is seeded by the test factory
        var response = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email = "user@test.com",
            password = "Str0ngTestPassw0rd!",
            firstName = "Dup",
            lastName = "Licate"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_ThenLogin_SucceedsWithSuppliedPassword()
    {
        // Arrange - create the user via the new endpoint
        var createResponse = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email = "login.roundtrip@test.com",
            password = "L0ginRoundTrip!",
            firstName = "Login",
            lastName = "RoundTrip"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var anonClient = _factory.CreateClient();

        // Act - login with the admin-supplied password
        var loginResponse = await anonClient.PostAsJsonAsync("/api/auth/login", new
        {
            email = "login.roundtrip@test.com",
            password = "L0ginRoundTrip!"
        });

        // Assert - credentials accepted (200; wrong credentials return 400)
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // And a wrong password is rejected
        var badLoginResponse = await anonClient.PostAsJsonAsync("/api/auth/login", new
        {
            email = "login.roundtrip@test.com",
            password = "WrongPassword123!"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badLoginResponse.StatusCode);
    }
}
