using System.Text.Json;
using IAM.API.Controllers;
using IAM.Core.Authority;
using IAM.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>Task 4844: request validation and wire format of POST /api/resolver/authority.</summary>
public class AuthorityResolverControllerTests
{
    private sealed class Stub : IAuthorityResolverService
    {
        public AuthorityRequest? Seen { get; private set; }
        public Task<AuthorityResolution> ResolveAsync(AuthorityRequest request, CancellationToken ct = default)
        {
            Seen = request;
            return Task.FromResult(new AuthorityResolution
            {
                State = AuthorityDecisionState.ContinuityAuthorized,
                Reasons = { new AuthorityReason("CONTINUITY_AUTHORIZED", "x") },
                EvaluatedAtUtc = DateTime.UtcNow
            });
        }
    }

    private static AuthorityResolverController.AuthorityResolveBody Body() => new()
    {
        TenantId = Guid.NewGuid(), Domain = "deploys", ActorUserId = Guid.NewGuid()
    };

    [Fact]
    public async Task Missing_required_fields_are_400()
    {
        var c = new AuthorityResolverController(new Stub());
        var b = Body(); b.TenantId = Guid.Empty;
        Assert.IsType<BadRequestObjectResult>(await c.ResolveAsync(b, default));
        b = Body(); b.Domain = " ";
        Assert.IsType<BadRequestObjectResult>(await c.ResolveAsync(b, default));
        b = Body(); b.ActorUserId = Guid.Empty;
        Assert.IsType<BadRequestObjectResult>(await c.ResolveAsync(b, default));
    }

    [Fact]
    public async Task Bad_context_enums_are_400()
    {
        var c = new AuthorityResolverController(new Stub());
        var b = Body(); b.Context = new() { Urgency = "yesterday" };
        Assert.IsType<BadRequestObjectResult>(await c.ResolveAsync(b, default));
        b = Body(); b.Context = new() { HumanAvailability = "maybe" };
        Assert.IsType<BadRequestObjectResult>(await c.ResolveAsync(b, default));
    }

    [Fact]
    public async Task Decision_is_returned_in_upper_snake_case_and_context_is_mapped()
    {
        var stub = new Stub();
        var b = Body();
        b.Context = new() { Urgency = "CRITICAL", Reversible = true, HumanAvailability = "unavailable", Evidence = new() { "e1" } };
        var ok = Assert.IsType<OkObjectResult>(await new AuthorityResolverController(stub).ResolveAsync(b, default));

        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("CONTINUITY_AUTHORIZED", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal("CONTINUITY_AUTHORIZED", doc.RootElement.GetProperty("reasons")[0].GetProperty("code").GetString());
        Assert.Equal(AuthorityLevel.Critical, stub.Seen!.Context.Urgency);
        Assert.Equal(HumanAvailability.Unavailable, stub.Seen.Context.HumanAvailability);
        Assert.Equal("deploys", stub.Seen.DomainKey);
    }
}
