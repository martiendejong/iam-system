using IAM.Core.Authority;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace IAM.API.Controllers;

/// <summary>
/// Canonical authority resolver (task 4844): effective command authority and escalation.
/// Same access rule as the who-decides resolver: only the vault client with scope "iam_resolver".
/// Always answers 200 with an explicit decision (a DENY is a valid answer, not an error); 400 is
/// reserved for malformed requests.
/// </summary>
[ApiController]
[Route("api/resolver/authority")]
[Authorize(
    AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    Policy = ResolverController.PolicyName)]
public class AuthorityResolverController : ControllerBase
{
    private readonly IAuthorityResolverService _resolver;

    public AuthorityResolverController(IAuthorityResolverService resolver)
    {
        _resolver = resolver;
    }

    public sealed class AuthorityResolveBody
    {
        public Guid TenantId { get; set; }
        public string? Domain { get; set; }
        public string? ProjectKey { get; set; }
        public string? ResourceType { get; set; }
        public string? ResourceId { get; set; }
        public string? Action { get; set; }
        public Guid ActorUserId { get; set; }
        public AuthorityBodyContext? Context { get; set; }
        /// <summary>Optional evaluation moment, to reconstruct a past decision.</summary>
        public DateTime? AtUtc { get; set; }
    }

    public sealed class AuthorityBodyContext
    {
        /// <summary>low, medium, high or critical.</summary>
        public string? Urgency { get; set; }
        public string? Severity { get; set; }
        public bool? Reversible { get; set; }
        public List<string>? Evidence { get; set; }
        /// <summary>available, unavailable or unknown.</summary>
        public string? HumanAvailability { get; set; }
        public int? CurrentStageOrder { get; set; }
        public DateTime? StageEngagedAtUtc { get; set; }
        public Dictionary<string, object>? Attributes { get; set; }
    }

    /// <summary>Resolves who holds authority for the request and whether the actor may proceed.</summary>
    [HttpPost]
    public async Task<IActionResult> ResolveAsync([FromBody] AuthorityResolveBody body, CancellationToken ct)
    {
        if (body.TenantId == Guid.Empty) return BadRequest(new { error = "tenantId is required" });
        if (string.IsNullOrWhiteSpace(body.Domain)) return BadRequest(new { error = "domain is required" });
        if (body.ActorUserId == Guid.Empty) return BadRequest(new { error = "actorUserId is required" });

        var c = body.Context;
        if (!TryLevel(c?.Urgency, out var urgency)) return BadRequest(new { error = "context.urgency must be low, medium, high or critical" });
        if (!TryLevel(c?.Severity, out var severity)) return BadRequest(new { error = "context.severity must be low, medium, high or critical" });
        if (!TryAvailability(c?.HumanAvailability, out var availability))
            return BadRequest(new { error = "context.humanAvailability must be available, unavailable or unknown" });

        var result = await _resolver.ResolveAsync(new AuthorityRequest
        {
            TenantId = body.TenantId,
            DomainKey = body.Domain!,
            ProjectKey = body.ProjectKey,
            ResourceType = body.ResourceType,
            ResourceId = body.ResourceId,
            Action = body.Action,
            ActorUserId = body.ActorUserId,
            AtUtc = body.AtUtc?.ToUniversalTime(),
            Context = new AuthorityRequestContext
            {
                Urgency = urgency,
                Severity = severity,
                Reversible = c?.Reversible,
                Evidence = c?.Evidence ?? new List<string>(),
                HumanAvailability = availability,
                CurrentStageOrder = c?.CurrentStageOrder,
                StageEngagedAtUtc = c?.StageEngagedAtUtc?.ToUniversalTime(),
                Attributes = c?.Attributes
            }
        }, ct);

        return Ok(new
        {
            decision = ToWire(result.State),
            reasons = result.Reasons,
            policy = result.Policy,
            stage = result.Stage,
            authorities = result.Authorities,
            requiredApprovals = result.RequiredApprovals,
            escalationTarget = result.EscalationTarget,
            deadlineUtc = result.DeadlineUtc,
            continuityPolicyRef = result.ContinuityPolicyRef,
            evaluatedAtUtc = result.EvaluatedAtUtc,
            resolverVersion = result.ResolverVersion
        });
    }

    public static string ToWire(AuthorityDecisionState s) => s switch
    {
        AuthorityDecisionState.Allow => "ALLOW",
        AuthorityDecisionState.Deny => "DENY",
        AuthorityDecisionState.RequiresApproval => "REQUIRES_APPROVAL",
        AuthorityDecisionState.Escalate => "ESCALATE",
        AuthorityDecisionState.ContinuityAuthorized => "CONTINUITY_AUTHORIZED",
        _ => "DENY"
    };

    private static bool TryLevel(string? v, out AuthorityLevel level)
    {
        level = AuthorityLevel.Unspecified;
        if (string.IsNullOrWhiteSpace(v)) return true;
        return Enum.TryParse(v, true, out level) && level != AuthorityLevel.Unspecified && Enum.IsDefined(level);
    }

    private static bool TryAvailability(string? v, out HumanAvailability a)
    {
        a = HumanAvailability.Unknown;
        if (string.IsNullOrWhiteSpace(v)) return true;
        return Enum.TryParse(v, true, out a) && Enum.IsDefined(a);
    }
}
