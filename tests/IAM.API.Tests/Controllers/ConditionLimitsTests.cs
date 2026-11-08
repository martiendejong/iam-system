using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Configuration;
using IAM.Core.Services;
using IAM.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5577: the condition evaluator has hard limits (count, lengths, linear-time regex) and the
/// evaluate endpoint is limited to people who manage policies.
/// </summary>
public sealed class ConditionLimitsTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public ConditionLimitsTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private static ConditionEvaluator NewEvaluator(ConditionLimitsOptions? o = null) =>
        new(NullLogger<ConditionEvaluator>.Instance, Options.Create(o ?? new ConditionLimitsOptions()));

    private static PolicyCondition Matches(string pattern) =>
        new() { Field = "user.name", Operator = "matches", Value = pattern };

    private static ConditionContext Ctx(string name) =>
        new() { UserAttributes = new Dictionary<string, object> { ["name"] = name } };

    private HttpClient ClientAs(params string[] roles)
    {
        var id = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuthenticationHelper.GenerateJwtToken(id, $"{id:N}@cond-limits.test", roles, null));
        return client;
    }

    private static object Body(int conditions, string pattern = "^a+$") => new
    {
        conditions = Enumerable.Range(0, conditions)
            .Select(_ => new { field = "user.name", @operator = "matches", value = pattern }).ToList(),
        context = new { userAttributes = new Dictionary<string, object> { ["name"] = "aaa" } }
    };

    [Fact]
    public void CatastrophicPattern_FinishesQuickly()
    {
        var sw = Stopwatch.StartNew();
        var result = NewEvaluator().Evaluate(new List<PolicyCondition> { Matches("(a+)+$") },
            Ctx(new string('a', 100) + "!"));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
        Assert.False(result.Satisfied);
    }

    [Fact]
    public void NormalPattern_StillMatches()
    {
        var result = NewEvaluator().Evaluate(new List<PolicyCondition> { Matches("^jo.*@example[.]com$") },
            Ctx("john@example.com"));
        Assert.True(result.Satisfied);
    }

    [Fact]
    public void TooManyConditions_AreRejected()
    {
        var conditions = Enumerable.Range(0, 51).Select(_ => Matches("a")).ToList();
        Assert.False(NewEvaluator().CheckLimits(conditions, null, out var error));
        Assert.Contains("Too many conditions", error);
        Assert.False(NewEvaluator().Evaluate(conditions, Ctx("a")).Satisfied);
    }

    [Fact]
    public void OverlongPatternAndValue_AreRejected()
    {
        var e = NewEvaluator();
        Assert.False(e.CheckLimits(new[] { Matches(new string('a', 257)) }, null, out var err1));
        Assert.Contains("pattern", err1);

        var eq = new PolicyCondition { Field = "user.name", Operator = "eq", Value = new string('a', 2049) };
        Assert.False(e.CheckLimits(new[] { eq }, null, out var err2));
        Assert.Contains("value", err2);

        Assert.False(e.CheckLimits(new List<PolicyCondition>(), Ctx(new string('a', 2049)), out _));
    }

    [Fact]
    public void LookaroundAndBackreference_AreInvalid()
    {
        var e = NewEvaluator();
        Assert.False(e.ValidateConditions("[{\"field\":\"user.name\",\"operator\":\"matches\",\"value\":\"(?=a)a\"}]", out var err));
        Assert.NotNull(err);
        Assert.False(e.ValidateConditions("[{\"field\":\"user.name\",\"operator\":\"matches\",\"value\":\"(a)\\\\1\"}]", out _));
        Assert.True(e.ValidateConditions("[{\"field\":\"user.name\",\"operator\":\"matches\",\"value\":\"^a+$\"}]", out _));
    }

    [Fact]
    public async Task Evaluate_OverCap_Returns400()
    {
        var response = await ClientAs("SuperAdmin").PostAsJsonAsync("/api/conditions/evaluate", Body(51));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Evaluate_NonAdmin_Returns403_PolicyAdmin200()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ClientAs("User").PostAsJsonAsync("/api/conditions/evaluate", Body(1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await ClientAs("BuildingManager").PostAsJsonAsync("/api/conditions/evaluate", Body(1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await ClientAs("SuperAdmin").PostAsJsonAsync("/api/conditions/evaluate", Body(1))).StatusCode);
    }

    [Fact]
    public async Task Evaluate_CatastrophicPattern_ReturnsQuickly()
    {
        // Warm up the test host so startup time is not counted.
        await ClientAs("SuperAdmin").PostAsJsonAsync("/api/conditions/evaluate", Body(1));

        var sw = Stopwatch.StartNew();
        var response = await ClientAs("SuperAdmin").PostAsJsonAsync("/api/conditions/evaluate", new
        {
            conditions = new[] { new { field = "user.name", @operator = "matches", value = "(a+)+$" } },
            context = new { userAttributes = new Dictionary<string, object> { ["name"] = new string('a', 200) + "!" } }
        });
        sw.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void PolicyConditionMap_OverCap_IsRejected()
    {
        var o = new ConditionLimitsOptions();
        var map = Enumerable.Range(0, 51).ToDictionary(i => $"k{i}", i => (object)"v");
        Assert.NotNull(o.CheckPolicyConditions(map));
        Assert.Null(o.CheckPolicyConditions(new Dictionary<string, object> { ["device_health"] = "ok" }));
    }
}
