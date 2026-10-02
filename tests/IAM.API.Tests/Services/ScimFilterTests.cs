using IAM.API.Controllers;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4710: a SCIM filter that is not one "attribute operator value" term and has no " and "
/// used to make ApplyUserFilter call itself on the same text until the stack overflowed, which
/// kills the whole IAM process. Malformed filters are now a 400 invalidFilter, never a hang and
/// never an unfiltered list. (On the old code the bad-filter cases crash the test host.)
/// </summary>
public class ScimFilterTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static IAMDbContext CreateContext() => new(new DbContextOptionsBuilder<IAMDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<ScimService> CreateServiceWithUsersAsync(IAMDbContext context)
    {
        foreach (var (email, first, active) in new[]
                 {
                     ("ann@example.com", "Ann", true),
                     ("bob@example.com", "Bob", true),
                     ("cat@other.org", "Cat", false)
                 })
        {
            context.Users.Add(new User { Email = email, FirstName = first, LastName = "T", PasswordHash = "x", IsActive = active });
        }
        context.Groups.Add(new Group { Name = "Admins", TenantId = TenantId, IsActive = true });
        context.Groups.Add(new Group { Name = "Staff", TenantId = TenantId, IsActive = true });
        await context.SaveChangesAsync();
        return new ScimService(context);
    }

    private static async Task<T> WithinAsync<T>(Task<T> task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == task, "filter handling did not terminate");
        return await task;
    }

    public static IEnumerable<object[]> MalformedFilters() => new[]
    {
        new object[] { "foo" },
        new object[] { "foo and bar" },
        new object[] { "a and" },
        new object[] { " and " },
        new object[] { "and" },
        new object[] { "userName eq \"ann@example.com\" and foo" },
        new object[] { "userName eq" + new string('x', 5000) },
        new object[] { new string('a', 100_000) },
        new object[] { string.Join(" and ", Enumerable.Repeat("active eq true", 11)) },
        new object[] { "unknownAttr eq \"x\"" },
        new object[] { "userName gt \"x\"" },
        new object[] { "active eq maybe" },
        new object[] { "id eq not-a-guid" },
        new object[] { "meta.created gt notadate" },
    };

    [Theory]
    [MemberData(nameof(MalformedFilters))]
    public async Task ListUsers_MalformedOrUnsupportedFilter_ThrowsScimFilterException(string filter)
    {
        var service = await CreateServiceWithUsersAsync(CreateContext());

        await Assert.ThrowsAsync<ScimFilterException>(() =>
            WithinAsync(service.ListUsersAsync(TenantId, new ScimQueryOptions { Filter = filter })));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("foo and bar")]
    [InlineData("displayName")]
    [InlineData("a and")]
    [InlineData("unknown eq \"x\"")]
    [InlineData("id eq not-a-guid")]
    public async Task ListGroups_MalformedOrUnsupportedFilter_ThrowsScimFilterException(string filter)
    {
        var service = await CreateServiceWithUsersAsync(CreateContext());

        await Assert.ThrowsAsync<ScimFilterException>(() =>
            WithinAsync(service.ListGroupsAsync(TenantId, new ScimQueryOptions { Filter = filter })));
    }

    [Theory]
    [InlineData("userName eq \"ann@example.com\"", 1)]
    [InlineData("userName co \"example.com\"", 2)]
    [InlineData("userName sw \"cat\"", 1)]
    [InlineData("active eq true", 2)]
    [InlineData("active eq false", 1)]
    [InlineData("userName co \"example.com\" and name.givenName eq \"Bob\"", 1)]
    [InlineData("active eq true AND userName co \"example.com\"", 2)]
    [InlineData("displayName eq \"a and b\"", 0)]
    public async Task ListUsers_ValidFilters_ReturnSameResultsAsBefore(string filter, int expected)
    {
        var service = await CreateServiceWithUsersAsync(CreateContext());

        var result = await WithinAsync(service.ListUsersAsync(TenantId, new ScimQueryOptions { Filter = filter }));

        Assert.Equal(expected, result.TotalResults);
    }

    [Fact]
    public async Task ListUsers_NoFilter_ReturnsEveryone()
    {
        var service = await CreateServiceWithUsersAsync(CreateContext());

        Assert.Equal(3, (await service.ListUsersAsync(TenantId, new ScimQueryOptions())).TotalResults);
        Assert.Equal(3, (await service.ListUsersAsync(TenantId, new ScimQueryOptions { Filter = "  " })).TotalResults);
    }

    [Fact]
    public async Task ListUsers_FilterAtTheLimits_IsAccepted()
    {
        var service = await CreateServiceWithUsersAsync(CreateContext());
        var tenTerms = string.Join(" and ", Enumerable.Repeat("active eq true", 10));

        var result = await service.ListUsersAsync(TenantId, new ScimQueryOptions { Filter = tenTerms });

        Assert.Equal(2, result.TotalResults);
    }

    [Theory]
    [InlineData("displayName eq \"Admins\"", 1)]
    [InlineData("displayName sw \"S\"", 1)]
    [InlineData("displayName co \"s\"", 1)]
    public async Task ListGroups_ValidFilters_ReturnSameResultsAsBefore(string filter, int expected)
    {
        var service = await CreateServiceWithUsersAsync(CreateContext());

        var result = await service.ListGroupsAsync(TenantId, new ScimQueryOptions { Filter = filter });

        Assert.Equal(expected, result.TotalResults);
    }

    [Theory]
    [InlineData("/scim/v2/Users")]
    [InlineData("/scim/v2/Groups")]
    public async Task Controller_BadFilter_Returns400WithInvalidFilterScimError(string kind)
    {
        var context = CreateContext();
        var service = await CreateServiceWithUsersAsync(context);
        var (_, bearer) = await service.CreateTokenAsync(TenantId, "t", null, null);
        var controller = new ScimController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = $"Bearer {bearer}";

        var response = kind.EndsWith("Users")
            ? await controller.ListUsers("foo")
            : await controller.ListGroups("foo");

        var bad = Assert.IsType<BadRequestObjectResult>(response);
        var error = Assert.IsType<ScimError>(bad.Value);
        Assert.Equal(400, error.Status);
        Assert.Equal("invalidFilter", error.ScimType);
        Assert.Contains("urn:ietf:params:scim:api:messages:2.0:Error", error.Schemas);

        // the service is still up and a valid request still works afterwards
        var ok = Assert.IsType<OkObjectResult>(await controller.ListUsers("userName eq \"ann@example.com\""));
        Assert.Equal(1, Assert.IsType<ScimListResponse<ScimUserResource>>(ok.Value).TotalResults);
    }
}
