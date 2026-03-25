using System.Text.Json;
using IAM.Core.Services;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// API endpoints for ABAC condition evaluation, validation, and metadata.
/// Provides tools for testing and debugging policy conditions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ConditionsController : ControllerBase
{
    private readonly IConditionEvaluator _conditionEvaluator;
    private readonly ILogger<ConditionsController> _logger;

    public ConditionsController(
        IConditionEvaluator conditionEvaluator,
        ILogger<ConditionsController> logger)
    {
        _conditionEvaluator = conditionEvaluator;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate conditions against a provided context.
    /// Use this to test whether a set of conditions would be satisfied for a given request context.
    /// </summary>
    [HttpPost("evaluate")]
    [ProducesResponseType(typeof(ConditionEvaluationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<ConditionEvaluationResult> Evaluate([FromBody] EvaluateConditionsRequest request)
    {
        if (request.Conditions == null || request.Conditions.Count == 0)
        {
            return Ok(new ConditionEvaluationResult { Satisfied = true });
        }

        if (request.Context == null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Missing context",
                Detail = "A condition context with attributes is required for evaluation",
                Status = StatusCodes.Status400BadRequest
            });
        }

        _logger.LogDebug("Evaluating {Count} conditions", request.Conditions.Count);

        var result = _conditionEvaluator.Evaluate(request.Conditions, request.Context);
        return Ok(result);
    }

    /// <summary>
    /// Validate condition JSON syntax and semantics.
    /// Checks that all fields, operators, and values are well-formed without executing evaluation.
    /// </summary>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(ValidateConditionsResponse), StatusCodes.Status200OK)]
    public ActionResult<ValidateConditionsResponse> Validate([FromBody] ValidateConditionsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Conditions))
        {
            return Ok(new ValidateConditionsResponse
            {
                Valid = true,
                Message = "Empty conditions are valid (no restrictions)"
            });
        }

        var isValid = _conditionEvaluator.ValidateConditions(request.Conditions, out var error);

        return Ok(new ValidateConditionsResponse
        {
            Valid = isValid,
            Error = error,
            Message = isValid ? "Conditions are valid" : "Validation failed"
        });
    }

    /// <summary>
    /// List all supported condition operators with descriptions.
    /// </summary>
    [HttpGet("operators")]
    [ProducesResponseType(typeof(List<OperatorInfo>), StatusCodes.Status200OK)]
    public ActionResult<List<OperatorInfo>> GetOperators()
    {
        var operators = ConditionEvaluator.SupportedOperators
            .Select(kv => new OperatorInfo
            {
                Name = kv.Key,
                Description = kv.Value,
                ValueType = GetOperatorValueType(kv.Key)
            })
            .ToList();

        return Ok(operators);
    }

    /// <summary>
    /// List all known attribute paths with descriptions.
    /// Note: custom attributes beyond this list are also supported.
    /// </summary>
    [HttpGet("attributes")]
    [ProducesResponseType(typeof(List<AttributeInfo>), StatusCodes.Status200OK)]
    public ActionResult<List<AttributeInfo>> GetAttributes()
    {
        var attributes = ConditionEvaluator.KnownAttributes
            .Select(kv =>
            {
                var parts = kv.Key.Split('.', 2);
                return new AttributeInfo
                {
                    Path = kv.Key,
                    Category = parts[0],
                    Name = parts.Length > 1 ? parts[1] : kv.Key,
                    Description = kv.Value
                };
            })
            .OrderBy(a => a.Category)
            .ThenBy(a => a.Name)
            .ToList();

        return Ok(attributes);
    }

    #region Private Helpers

    private static string GetOperatorValueType(string op)
    {
        return op switch
        {
            "eq" or "neq" => "string | number | boolean",
            "gt" or "gte" or "lt" or "lte" => "number | datetime",
            "in" or "not_in" => "array",
            "contains" or "starts_with" or "ends_with" => "string",
            "matches" => "string (regex pattern)",
            "between" => "array [min, max]",
            "ip_range" => "string (CIDR notation, e.g., '192.168.1.0/24')",
            _ => "any"
        };
    }

    #endregion
}

#region Request/Response DTOs

/// <summary>
/// Request body for condition evaluation.
/// </summary>
public class EvaluateConditionsRequest
{
    /// <summary>
    /// List of conditions to evaluate.
    /// </summary>
    public List<PolicyCondition> Conditions { get; set; } = new();

    /// <summary>
    /// Context containing attributes to evaluate conditions against.
    /// </summary>
    public ConditionContext? Context { get; set; }
}

/// <summary>
/// Request body for condition validation.
/// </summary>
public class ValidateConditionsRequest
{
    /// <summary>
    /// JSON string of conditions to validate.
    /// </summary>
    public string Conditions { get; set; } = string.Empty;
}

/// <summary>
/// Response for condition validation.
/// </summary>
public class ValidateConditionsResponse
{
    public bool Valid { get; set; }
    public string? Error { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about a supported operator.
/// </summary>
public class OperatorInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ValueType { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about a known attribute path.
/// </summary>
public class AttributeInfo
{
    public string Path { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

#endregion
