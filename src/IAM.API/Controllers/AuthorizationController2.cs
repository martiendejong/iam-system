using IAM.API.Authorization;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// Unified authorization endpoints for evaluating access for any principal type (user or device).
/// Provides single-request evaluation, batch evaluation, effective permissions lookup,
/// and a test/debug endpoint for authorization troubleshooting.
///
/// Task 5162: a caller can ask about itself; asking about anyone else in a tenant needs an administrator of that
/// tenant or a service account holding authorize:evaluate. The caller comes from the token (see
/// <see cref="IEvaluationAccessResolver"/>), never from the body. Only those privileged callers get the evaluation
/// path and the matched policy/permission and the reason that names them.
/// </summary>
[ApiController]
[Route("api/authorize")]
[Authorize]
public class AuthorizationController2 : ControllerBase
{
    private const string ForbiddenMessage =
        "You can only evaluate your own permissions. Evaluating another principal needs an administrator of the " +
        "tenant or a service account with the authorize:evaluate permission.";

    private readonly IUnifiedAuthorizationService _authorizationService;
    private readonly IEvaluationAccessResolver _accessResolver;
    private readonly ILogger<AuthorizationController2> _logger;

    public AuthorizationController2(
        IUnifiedAuthorizationService authorizationService,
        IEvaluationAccessResolver accessResolver,
        ILogger<AuthorizationController2> logger)
    {
        _authorizationService = authorizationService;
        _accessResolver = accessResolver;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate a single authorization request.
    /// Returns whether the principal (user or device) is allowed to perform the action on the resource.
    /// </summary>
    /// <remarks>
    /// Sample request:
    ///
    ///     POST /api/authorize
    ///     {
    ///         "principalType": "user",
    ///         "principalId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    ///         "tenantId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    ///         "resource": "Door",
    ///         "action": "Unlock"
    ///     }
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(AuthorizeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Evaluate(
        [FromBody] AuthorizeRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var caller = await _accessResolver.ResolveAsync(User, ct);
        var access = caller.Check(request.PrincipalType, request.PrincipalId, request.TenantId);
        if (access == EvaluationAccess.Denied)
            return RefuseQuery(request.PrincipalType, request.PrincipalId, request.TenantId);

        if (string.IsNullOrWhiteSpace(request.Resource))
            return BadRequest(new { error = "Resource is required" });

        var authRequest = MapToAuthorizationRequest(request);
        var decision = await _authorizationService.EvaluateAsync(authRequest, ct);

        return Ok(ToResponse(decision, access));
    }

    /// <summary>
    /// Evaluate multiple authorization requests in a single batch.
    /// All requests are processed in parallel for optimal performance.
    /// </summary>
    /// <remarks>
    /// Sample request:
    ///
    ///     POST /api/authorize/batch
    ///     {
    ///         "requests": [
    ///             {
    ///                 "principalType": "user",
    ///                 "principalId": "...",
    ///                 "tenantId": "...",
    ///                 "resource": "Door",
    ///                 "action": "Unlock"
    ///             },
    ///             {
    ///                 "principalType": "device",
    ///                 "principalId": "...",
    ///                 "tenantId": "...",
    ///                 "resource": "acme:hq:floor-3:sensor:temp",
    ///                 "action": "read"
    ///             }
    ///         ]
    ///     }
    /// </remarks>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(BatchAuthorizeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> EvaluateBatch(
        [FromBody] BatchAuthorizeRequest request,
        CancellationToken ct)
    {
        if (request.Requests == null || request.Requests.Count == 0)
            return BadRequest(new { error = "At least one request is required" });

        if (request.Requests.Count > 100)
            return BadRequest(new { error = "Maximum 100 requests per batch" });

        if (request.Requests.Any(r => r == null))
            return BadRequest(new { error = "Batch requests must not be null" });

        // Every item is judged before anything is evaluated: one disallowed item rejects the whole batch, so the
        // batch can never be used to probe a mix of allowed and foreign targets.
        var caller = await _accessResolver.ResolveAsync(User, ct);
        var accesses = request.Requests
            .Select(r => caller.Check(r.PrincipalType, r.PrincipalId, r.TenantId))
            .ToList();

        var refusedIndex = accesses.IndexOf(EvaluationAccess.Denied);
        if (refusedIndex >= 0)
        {
            var refused = request.Requests[refusedIndex];
            return RefuseQuery(refused.PrincipalType, refused.PrincipalId, refused.TenantId);
        }

        var authRequests = request.Requests
            .Select(MapToAuthorizationRequest)
            .ToList();

        var decisions = await _authorizationService.EvaluateBatchAsync(authRequests, ct);

        return Ok(new BatchAuthorizeResponse
        {
            Results = decisions.Select((d, i) => ToResponse(d, accesses[i])).ToList(),
            TotalAllowed = decisions.Count(d => d.Allowed),
            TotalDenied = decisions.Count(d => !d.Allowed)
        });
    }

    /// <summary>
    /// Get all effective permissions for a principal (user or device) in a tenant context.
    /// Returns direct, inherited, and group permissions, plus the final merged list after deny overrides.
    /// </summary>
    [HttpGet("permissions/{principalType}/{principalId}")]
    [ProducesResponseType(typeof(EffectivePermissionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetEffectivePermissions(
        string principalType,
        Guid principalId,
        [FromQuery] Guid tenantId,
        CancellationToken ct)
    {
        // A caller's own permissions are theirs to read; anyone else's are an administrator's. Checked before the
        // validation below so a refused caller learns nothing about which ids or types are valid.
        var caller = await _accessResolver.ResolveAsync(User, ct);
        if (caller.Check(principalType, principalId, tenantId) == EvaluationAccess.Denied)
            return RefuseQuery(principalType, principalId, tenantId);

        if (principalId == Guid.Empty)
            return BadRequest(new { error = "Principal ID is required" });

        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "Tenant ID is required (query parameter)" });

        var validTypes = new[] { "user", "device" };
        if (!validTypes.Contains(principalType.ToLowerInvariant()))
            return BadRequest(new { error = $"Invalid principal type '{principalType}'. Supported: user, device" });

        var permissions = await _authorizationService.GetEffectivePermissionsAsync(
            principalType, principalId, tenantId, ct);

        return Ok(new EffectivePermissionsResponse
        {
            PrincipalType = permissions.PrincipalType,
            PrincipalId = permissions.PrincipalId,
            DirectPermissions = permissions.DirectPermissions,
            InheritedPermissions = permissions.InheritedPermissions,
            GroupPermissions = permissions.GroupPermissions,
            DeniedPermissions = permissions.DeniedPermissions,
            EffectiveAllowed = permissions.EffectiveAllowed,
            TotalEffective = permissions.EffectiveAllowed.Count
        });
    }

    /// <summary>
    /// Test authorization with extra debug information.
    /// Same as evaluate, but includes the full evaluation path for troubleshooting.
    /// This endpoint is intended for administrators debugging authorization issues.
    /// </summary>
    /// <remarks>
    /// Sample request:
    ///
    ///     POST /api/authorize/test
    ///     {
    ///         "principalType": "user",
    ///         "principalId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    ///         "tenantId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    ///         "resource": "Camera",
    ///         "action": "View",
    ///         "context": {
    ///             "ip_address": "10.0.0.5",
    ///             "device_health": "trusted"
    ///         }
    ///     }
    /// </remarks>
    [HttpPost("test")]
    [ProducesResponseType(typeof(TestAuthorizeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> TestAuthorization(
        [FromBody] AuthorizeRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var caller = await _accessResolver.ResolveAsync(User, ct);
        var access = caller.Check(request.PrincipalType, request.PrincipalId, request.TenantId);
        if (access == EvaluationAccess.Denied)
            return RefuseQuery(request.PrincipalType, request.PrincipalId, request.TenantId);

        if (string.IsNullOrWhiteSpace(request.Resource))
            return BadRequest(new { error = "Resource is required" });

        _logger.LogInformation(
            "Authorization test: {PrincipalType}={PrincipalId}, Resource={Resource}, Action={Action}",
            request.PrincipalType, request.PrincipalId, request.Resource, request.Action);

        var authRequest = MapToAuthorizationRequest(request);
        var decision = await _authorizationService.EvaluateAsync(authRequest, ct);
        var response = ToResponse(decision, access);

        return Ok(new TestAuthorizeResponse
        {
            Allowed = response.Allowed,
            Reason = response.Reason,
            MatchedPolicy = response.MatchedPolicy,
            MatchedPermission = response.MatchedPermission,
            PrincipalType = response.PrincipalType,
            EvaluationTimeMs = response.EvaluationTimeMs,
            // The trace names policies, roles and groups: administrators only.
            EvaluationPath = access == EvaluationAccess.Admin ? decision.EvaluationPath : new List<string>(),
            Request = new AuthorizeRequestEcho
            {
                PrincipalType = request.PrincipalType,
                PrincipalId = request.PrincipalId,
                TenantId = request.TenantId,
                Resource = request.Resource,
                Action = request.Action,
                Context = request.Context
            },
            EvaluatedAt = DateTime.UtcNow
        });
    }

    #region Helper Methods

    private ObjectResult RefuseQuery(string? principalType, Guid principalId, Guid tenantId)
    {
        _logger.LogWarning(
            "Authorization query refused: caller {Caller} asked about {PrincipalType}={PrincipalId} in tenant {TenantId}",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value ?? "unknown",
            principalType, principalId, tenantId);

        return StatusCode(StatusCodes.Status403Forbidden, new { error = ForbiddenMessage });
    }

    /// <summary>
    /// The decision as the caller may see it. The reason and matched policy/permission name policies, roles and
    /// groups ("Allowed by policy 'X'"), so a caller asking about itself gets the verdict and a neutral reason only.
    /// </summary>
    private static AuthorizeResponse ToResponse(AuthorizationDecision decision, EvaluationAccess access)
    {
        var detailed = access == EvaluationAccess.Admin;

        return new AuthorizeResponse
        {
            Allowed = decision.Allowed,
            Reason = detailed ? decision.Reason : decision.Allowed ? "Access allowed" : "Access denied",
            MatchedPolicy = detailed ? decision.MatchedPolicy : null,
            MatchedPermission = detailed ? decision.MatchedPermission : null,
            PrincipalType = decision.PrincipalType,
            EvaluationTimeMs = decision.EvaluationTimeMs
        };
    }

    private static AuthorizationRequest MapToAuthorizationRequest(AuthorizeRequest request)
    {
        return new AuthorizationRequest
        {
            PrincipalType = request.PrincipalType,
            PrincipalId = request.PrincipalId,
            TenantId = request.TenantId,
            Resource = request.Resource,
            Action = request.Action,
            Context = request.Context
        };
    }

    #endregion
}

#region Request/Response DTOs

/// <summary>
/// Request DTO for single authorization evaluation.
/// </summary>
public class AuthorizeRequest
{
    /// <summary>
    /// Type of principal: "user" or "device". Defaults to "user".
    /// </summary>
    public string PrincipalType { get; set; } = "user";

    /// <summary>
    /// The principal's unique identifier (User.Id or Device.Id).
    /// </summary>
    public Guid PrincipalId { get; set; }

    /// <summary>
    /// Tenant context for the authorization check.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Resource being accessed (e.g., "Door", "Camera", "acme:hq:floor-3:sensor:temp-089").
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// Action being performed (e.g., "Unlock", "View", "read", "write").
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Optional ABAC context (ip_address, device_health, location, etc.).
    /// </summary>
    public Dictionary<string, object>? Context { get; set; }
}

/// <summary>
/// Request DTO for batch authorization evaluation.
/// </summary>
public class BatchAuthorizeRequest
{
    /// <summary>
    /// List of authorization requests to evaluate (max 100).
    /// </summary>
    public List<AuthorizeRequest> Requests { get; set; } = new();
}

/// <summary>
/// Response DTO for single authorization evaluation.
/// </summary>
public class AuthorizeResponse
{
    public bool Allowed { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? MatchedPolicy { get; set; }
    public string? MatchedPermission { get; set; }
    public string PrincipalType { get; set; } = string.Empty;
    public long EvaluationTimeMs { get; set; }
}

/// <summary>
/// Response DTO for batch authorization evaluation.
/// </summary>
public class BatchAuthorizeResponse
{
    public List<AuthorizeResponse> Results { get; set; } = new();
    public int TotalAllowed { get; set; }
    public int TotalDenied { get; set; }
}

/// <summary>
/// Response DTO for effective permissions query.
/// </summary>
public class EffectivePermissionsResponse
{
    public string PrincipalType { get; set; } = string.Empty;
    public Guid PrincipalId { get; set; }
    public List<string> DirectPermissions { get; set; } = new();
    public List<string> InheritedPermissions { get; set; } = new();
    public List<string> GroupPermissions { get; set; } = new();
    public List<string> DeniedPermissions { get; set; } = new();
    public List<string> EffectiveAllowed { get; set; } = new();
    public int TotalEffective { get; set; }
}

/// <summary>
/// Echo of the original request, included in test response for debugging.
/// </summary>
public class AuthorizeRequestEcho
{
    public string PrincipalType { get; set; } = string.Empty;
    public Guid PrincipalId { get; set; }
    public Guid TenantId { get; set; }
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public Dictionary<string, object>? Context { get; set; }
}

/// <summary>
/// Response DTO for test authorization with full debug trace.
/// </summary>
public class TestAuthorizeResponse
{
    public bool Allowed { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? MatchedPolicy { get; set; }
    public string? MatchedPermission { get; set; }
    public string PrincipalType { get; set; } = string.Empty;
    public long EvaluationTimeMs { get; set; }
    public List<string> EvaluationPath { get; set; } = new();
    public AuthorizeRequestEcho Request { get; set; } = new();
    public DateTime EvaluatedAt { get; set; }
}

#endregion
