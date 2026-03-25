using System.Text.Json.Serialization;

namespace IAM.Core.Services;

/// <summary>
/// Represents a single condition in an ABAC policy.
/// Conditions are evaluated against a request context to determine if a policy applies.
/// </summary>
public class PolicyCondition
{
    /// <summary>
    /// Dotted attribute path to evaluate.
    /// Format: "{category}.{attribute}" where category is user, resource, env, or action.
    /// Examples: "user.department", "resource.classification", "env.time", "env.ip", "action.type"
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Comparison operator to apply.
    /// Supported: eq, neq, gt, gte, lt, lte, in, not_in, contains, starts_with, ends_with, matches, between, ip_range
    /// </summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>
    /// Expected value for comparison. Can be a single value (string, number, bool)
    /// or a JSON array for operators like "in", "not_in", "between".
    /// </summary>
    public object Value { get; set; } = string.Empty;

    /// <summary>
    /// Logic operator for combining with the NEXT condition in the list.
    /// "and" (default when null) = both this and next must be true.
    /// "or" = either this or next must be true.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logic { get; set; }
}

/// <summary>
/// Request context containing all attributes for ABAC condition evaluation.
/// Attributes are organized by category: user, resource, environment, action.
/// </summary>
public class ConditionContext
{
    /// <summary>
    /// User attributes from identity/profile.
    /// Common keys: department, clearance_level, groups, title, location, manager
    /// </summary>
    public Dictionary<string, object> UserAttributes { get; set; } = new();

    /// <summary>
    /// Resource attributes describing the target being accessed.
    /// Common keys: classification, owner, type, sensitivity, created_at
    /// </summary>
    public Dictionary<string, object> ResourceAttributes { get; set; } = new();

    /// <summary>
    /// Environment/contextual attributes from the request.
    /// Common keys: time, ip, day_of_week, is_business_hours, geo_location
    /// </summary>
    public Dictionary<string, object> EnvironmentAttributes { get; set; } = new();

    /// <summary>
    /// Action attributes describing what operation is being performed.
    /// Common keys: type (read/write/delete/execute), scope (own/team/all)
    /// </summary>
    public Dictionary<string, object> ActionAttributes { get; set; } = new();

    /// <summary>
    /// Resolves an attribute value by dotted path.
    /// Supports "user.*", "resource.*", "env.*"/"environment.*", and "action.*" prefixes.
    /// </summary>
    /// <param name="path">Dotted path like "user.department" or "env.ip"</param>
    /// <returns>The attribute value, or null if not found</returns>
    public object? GetAttribute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var parts = path.Split('.', 2);
        if (parts.Length != 2)
            return null;

        var dict = parts[0].ToLowerInvariant() switch
        {
            "user" => UserAttributes,
            "resource" => ResourceAttributes,
            "env" or "environment" => EnvironmentAttributes,
            "action" => ActionAttributes,
            _ => null
        };

        return dict?.TryGetValue(parts[1], out var val) == true ? val : null;
    }
}

/// <summary>
/// Result of evaluating a set of policy conditions against a context.
/// Provides detailed information about which conditions matched or failed.
/// </summary>
public class ConditionEvaluationResult
{
    /// <summary>
    /// Whether all conditions were satisfied (considering logic operators).
    /// </summary>
    public bool Satisfied { get; set; }

    /// <summary>
    /// Human-readable descriptions of conditions that were satisfied.
    /// </summary>
    public List<string> MatchedConditions { get; set; } = new();

    /// <summary>
    /// Human-readable descriptions of conditions that failed.
    /// </summary>
    public List<string> FailedConditions { get; set; } = new();

    /// <summary>
    /// Error message if evaluation failed due to invalid conditions, null otherwise.
    /// </summary>
    public string? Error { get; set; }
}
