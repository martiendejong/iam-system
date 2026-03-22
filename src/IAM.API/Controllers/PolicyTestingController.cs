using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PolicyTestingController : ControllerBase
{
    private readonly IPolicyTestingService _testingService;
    private readonly ILogger<PolicyTestingController> _logger;

    public PolicyTestingController(
        IPolicyTestingService testingService,
        ILogger<PolicyTestingController> logger)
    {
        _testingService = testingService;
        _logger = logger;
    }

    /// <summary>
    /// Create a new policy test
    /// </summary>
    [HttpPost("tests")]
    public async Task<ActionResult<PolicyTest>> CreateTest(
        [FromBody] CreatePolicyTestRequest request,
        CancellationToken cancellationToken = default)
    {
        // Get user ID from claims
        var userIdClaim = User.FindFirst("sub")?.Value;
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
            request.TenantId,
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
        var tests = await _testingService.GetTestResultsAsync(skip: 0, take: 1, cancellationToken: cancellationToken);
        var test = tests.FirstOrDefault(t => t.Id == id);

        if (test == null)
            return NotFound();

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
        try
        {
            var test = await _testingService.ExecuteTestAsync(id, cancellationToken);
            return Ok(test);
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute test {TestId}", id);
            return StatusCode(500, "Test execution failed");
        }
    }

    /// <summary>
    /// Execute all tests for a policy
    /// </summary>
    [HttpPost("policies/{policyId}/execute-tests")]
    public async Task<ActionResult<List<PolicyTest>>> ExecuteTestsForPolicy(
        Guid policyId,
        CancellationToken cancellationToken = default)
    {
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
        var coverage = await _testingService.GetTestCoverageAsync(policyId, cancellationToken);
        return Ok(coverage);
    }

    /// <summary>
    /// Get test results with filtering
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
        var results = await _testingService.GetTestResultsAsync(
            policyId, passedOnly, since, skip, take, cancellationToken);

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
