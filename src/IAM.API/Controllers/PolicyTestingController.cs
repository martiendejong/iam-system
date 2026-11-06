using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// Task 5486: every action is decided from the caller's tenant-admin rows (ITenantAccessResolver, the same rule as the
/// Policy API of PR #195) in the tenant of the policy under test. [Authorize] is only the coarse gate. A caller who
/// administers no tenant gets 403 for every id, so ids are not probed; platform admins keep every tenant.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PolicyTestingController : ControllerBase
{
    private readonly IPolicyTestingService _testingService;
    private readonly ITenantAccessResolver _access;
    private readonly ILogger<PolicyTestingController> _logger;

    public PolicyTestingController(
        IPolicyTestingService testingService,
        ITenantAccessResolver access,
        ILogger<PolicyTestingController> logger)
    {
        _testingService = testingService;
        _access = access;
        _logger = logger;
    }

    private ObjectResult Refused() => StatusCode(StatusCodes.Status403Forbidden, new { error = "Forbidden" });

    /// <summary>Admin of the policy's tenant, or a failure result. Unknown policy: 404 (only reached by tenant admins).</summary>
    private async Task<(Guid TenantId, ActionResult? Failure)> AuthorizePolicyAsync(Guid policyId, CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanManageAny)
            return (Guid.Empty, Refused());

        var tenantId = await _testingService.GetPolicyTenantIdAsync(policyId, ct);
        if (tenantId == null)
            return (Guid.Empty, NotFound());

        if (!access.CanManage(tenantId.Value))
            return (Guid.Empty, Refused());

        return (tenantId.Value, null);
    }

    /// <summary>Admin of the tenant of the stored test (and of its policy's tenant), or a failure result.</summary>
    private async Task<(PolicyTest? Test, ActionResult? Failure)> AuthorizeTestAsync(Guid id, CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanManageAny)
            return (null, Refused());

        var test = await _testingService.GetTestAsync(id, ct);
        if (test == null)
            return (null, NotFound());

        var tenantId = test.Policy?.TenantId ?? test.TenantId;
        if (!access.CanManage(tenantId) || !access.CanManage(test.TenantId))
            return (null, Refused());

        return (test, null);
    }

    /// <summary>
    /// Create a new policy test
    /// </summary>
    [HttpPost("tests")]
    public async Task<ActionResult<PolicyTest>> CreateTest(
        [FromBody] CreatePolicyTestRequest request,
        CancellationToken cancellationToken = default)
    {
        var (policyTenantId, failure) = await AuthorizePolicyAsync(request.PolicyId, cancellationToken);
        if (failure != null)
            return failure;

        // The tenant is the policy's own; a different tenantId in the body is refused, never trusted.
        if (request.TenantId != policyTenantId)
            return BadRequest(new { error = "tenantId does not match the tenant of the policy" });

        // Get user ID from claims (try multiple claim types)
        var userIdClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        var test = await _testingService.CreateTestAsync(
            request.Name,
            request.Description,
            request.PolicyId,
            request.PolicyVersion,
            request.TestScenario,
            request.ExpectedResult,
            userId,
            policyTenantId,
            cancellationToken);

        return CreatedAtAction(nameof(GetTest), new { id = test.Id }, test);
    }

    /// <summary>
    /// Get a policy test by ID
    /// </summary>
    [HttpGet("tests/{id}")]
    public async Task<ActionResult<PolicyTest>> GetTest(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var (test, failure) = await AuthorizeTestAsync(id, cancellationToken);
        if (failure != null)
            return failure;

        return Ok(test);
    }

    /// <summary>
    /// Execute a policy test
    /// </summary>
    [HttpPost("tests/{id}/execute")]
    public async Task<ActionResult<PolicyTest>> ExecuteTest(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var (_, failure) = await AuthorizeTestAsync(id, cancellationToken);
        if (failure != null)
            return failure;

        try
        {
            var test = await _testingService.ExecuteTestAsync(id, cancellationToken);
            return Ok(test);
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute test {TestId}", id);
            return StatusCode(500, "Test execution failed");
        }
    }

    /// <summary>
    /// Execute the tests of a policy (at most PolicyTesting:MaxTestsPerRun per call, default 50)
    /// </summary>
    [HttpPost("policies/{policyId}/execute-tests")]
    public async Task<ActionResult<List<PolicyTest>>> ExecuteTestsForPolicy(
        Guid policyId,
        CancellationToken cancellationToken = default)
    {
        var (_, failure) = await AuthorizePolicyAsync(policyId, cancellationToken);
        if (failure != null)
            return failure;

        var results = await _testingService.ExecuteTestsForPolicyAsync(policyId, cancellationToken);
        return Ok(results);
    }

    /// <summary>
    /// Get test coverage for a policy
    /// </summary>
    [HttpGet("policies/{policyId}/coverage")]
    public async Task<ActionResult<PolicyTestCoverage>> GetTestCoverage(
        Guid policyId,
        CancellationToken cancellationToken = default)
    {
        var (_, failure) = await AuthorizePolicyAsync(policyId, cancellationToken);
        if (failure != null)
            return failure;

        var coverage = await _testingService.GetTestCoverageAsync(policyId, cancellationToken);
        return Ok(coverage);
    }

    /// <summary>
    /// Get test results with filtering; only tests of tenants the caller administers
    /// </summary>
    [HttpGet("tests")]
    public async Task<ActionResult<List<PolicyTest>>> GetTestResults(
        [FromQuery] Guid? policyId,
        [FromQuery] bool? passedOnly,
        [FromQuery] DateTime? since,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var access = await _access.ResolveAsync(User, cancellationToken);
        if (!access.CanManageAny)
            return Refused();

        // null ManageableTenants = platform admin: every tenant.
        var results = await _testingService.GetTestResultsAsync(
            policyId, passedOnly, since, skip, take, cancellationToken, access.ManageableTenants);

        return Ok(results);
    }

    /// <summary>
    /// Compare test results across policy versions
    /// </summary>
    [HttpGet("policies/{policyId}/compare")]
    public async Task<ActionResult<PolicyTestComparison>> CompareVersions(
        Guid policyId,
        [FromQuery] string version1,
        [FromQuery] string version2,
        CancellationToken cancellationToken = default)
    {
        var (_, failure) = await AuthorizePolicyAsync(policyId, cancellationToken);
        if (failure != null)
            return failure;

        if (string.IsNullOrWhiteSpace(version1) || string.IsNullOrWhiteSpace(version2))
            return BadRequest("Both version1 and version2 are required");

        var comparison = await _testingService.CompareVersionsAsync(
            policyId, version1, version2, cancellationToken);

        return Ok(comparison);
    }
}

public class CreatePolicyTestRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid PolicyId { get; set; }
    public string PolicyVersion { get; set; } = string.Empty;
    public string TestScenario { get; set; } = string.Empty;
    public string ExpectedResult { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
}
