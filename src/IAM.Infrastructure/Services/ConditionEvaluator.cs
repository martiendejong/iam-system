using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IAM.Core.Services;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Production implementation of the ABAC condition evaluation engine.
/// Supports 14 operators across string, numeric, DateTime, IP, and collection types.
/// Handles AND/OR logic combining between conditions.
/// </summary>
public class ConditionEvaluator : IConditionEvaluator
{
    private readonly ILogger<ConditionEvaluator> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// All supported operators with their descriptions (used by the API metadata endpoint).
    /// </summary>
    public static readonly Dictionary<string, string> SupportedOperators = new()
    {
        ["eq"] = "Equals - case-insensitive string comparison, numeric, or boolean equality",
        ["neq"] = "Not equals - inverse of eq",
        ["gt"] = "Greater than - numeric or DateTime comparison",
        ["gte"] = "Greater than or equal - numeric or DateTime comparison",
        ["lt"] = "Less than - numeric or DateTime comparison",
        ["lte"] = "Less than or equal - numeric or DateTime comparison",
        ["in"] = "In list - checks if value exists in the provided array",
        ["not_in"] = "Not in list - checks if value does NOT exist in the provided array",
        ["contains"] = "Contains - case-insensitive substring match",
        ["starts_with"] = "Starts with - case-insensitive prefix match",
        ["ends_with"] = "Ends with - case-insensitive suffix match",
        ["matches"] = "Regex match - evaluates value against a regular expression pattern",
        ["between"] = "Between - checks if value is between [min, max] inclusive (numeric or DateTime)",
        ["ip_range"] = "IP range - checks if IP address is within a CIDR range (e.g., 192.168.1.0/24)"
    };

    /// <summary>
    /// All known attribute paths with descriptions (used by the API metadata endpoint).
    /// </summary>
    public static readonly Dictionary<string, string> KnownAttributes = new()
    {
        // User attributes
        ["user.department"] = "User's department (e.g., 'Engineering', 'HR')",
        ["user.clearance_level"] = "User's security clearance level (numeric, e.g., 1-5)",
        ["user.groups"] = "User's group memberships (array of strings)",
        ["user.title"] = "User's job title",
        ["user.location"] = "User's office location",
        ["user.manager"] = "User's manager identifier",
        ["user.email"] = "User's email address",
        ["user.tenant_id"] = "User's current tenant context",

        // Resource attributes
        ["resource.classification"] = "Resource security classification (e.g., 'public', 'internal', 'confidential', 'secret')",
        ["resource.owner"] = "Resource owner identifier",
        ["resource.type"] = "Resource type (e.g., 'document', 'device', 'room')",
        ["resource.sensitivity"] = "Resource sensitivity level (numeric)",
        ["resource.created_at"] = "Resource creation timestamp (DateTime)",
        ["resource.tags"] = "Resource tags (array of strings)",

        // Environment attributes
        ["env.time"] = "Current time (HH:mm format or DateTime)",
        ["env.ip"] = "Request source IP address",
        ["env.day_of_week"] = "Current day of week (1=Monday, 7=Sunday)",
        ["env.is_business_hours"] = "Whether current time is within business hours (boolean)",
        ["env.geo_location"] = "Request geographic location",
        ["env.device_health"] = "Requesting device health status (e.g., 'trusted', 'compliant', 'unknown')",
        ["env.mfa_verified"] = "Whether MFA was completed for this session (boolean)",

        // Action attributes
        ["action.type"] = "Action being performed (e.g., 'read', 'write', 'delete', 'execute', 'control')",
        ["action.scope"] = "Scope of the action (e.g., 'own', 'team', 'all')"
    };

    public ConditionEvaluator(ILogger<ConditionEvaluator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ConditionEvaluationResult Evaluate(string conditionsJson, ConditionContext context)
    {
        if (string.IsNullOrWhiteSpace(conditionsJson))
        {
            return new ConditionEvaluationResult { Satisfied = true };
        }

        List<PolicyCondition> conditions;
        try
        {
            conditions = JsonSerializer.Deserialize<List<PolicyCondition>>(conditionsJson, JsonOptions)
                ?? new List<PolicyCondition>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize conditions JSON: {Json}", conditionsJson);
            return new ConditionEvaluationResult
            {
                Satisfied = false,
                Error = $"Invalid conditions JSON: {ex.Message}"
            };
        }

        return Evaluate(conditions, context);
    }

    /// <inheritdoc />
    public ConditionEvaluationResult Evaluate(List<PolicyCondition> conditions, ConditionContext context)
    {
        var result = new ConditionEvaluationResult();

        if (conditions.Count == 0)
        {
            result.Satisfied = true;
            return result;
        }

        // Evaluate each condition individually and collect results
        var evaluations = new List<(bool passed, string description)>();

        foreach (var condition in conditions)
        {
            var (passed, description) = EvaluateSingle(condition, context);
            evaluations.Add((passed, description));

            if (passed)
                result.MatchedConditions.Add(description);
            else
                result.FailedConditions.Add(description);
        }

        // Combine results using logic operators (AND/OR chaining)
        result.Satisfied = CombineResults(conditions, evaluations);

        return result;
    }

    /// <inheritdoc />
    public bool ValidateConditions(string conditionsJson, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(conditionsJson))
        {
            // Empty conditions are valid (no restrictions)
            return true;
        }

        List<PolicyCondition>? conditions;
        try
        {
            conditions = JsonSerializer.Deserialize<List<PolicyCondition>>(conditionsJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON format: {ex.Message}";
            return false;
        }

        if (conditions == null)
        {
            error = "Conditions must be a JSON array";
            return false;
        }

        for (var i = 0; i < conditions.Count; i++)
        {
            var condition = conditions[i];

            if (string.IsNullOrWhiteSpace(condition.Field))
            {
                error = $"Condition [{i}]: 'field' is required";
                return false;
            }

            var fieldParts = condition.Field.Split('.', 2);
            if (fieldParts.Length != 2)
            {
                error = $"Condition [{i}]: 'field' must be in 'category.attribute' format (e.g., 'user.department')";
                return false;
            }

            var category = fieldParts[0].ToLowerInvariant();
            if (category is not ("user" or "resource" or "env" or "environment" or "action"))
            {
                error = $"Condition [{i}]: Unknown field category '{fieldParts[0]}'. Must be one of: user, resource, env, environment, action";
                return false;
            }

            if (string.IsNullOrWhiteSpace(condition.Operator))
            {
                error = $"Condition [{i}]: 'operator' is required";
                return false;
            }

            if (!SupportedOperators.ContainsKey(condition.Operator.ToLowerInvariant()))
            {
                error = $"Condition [{i}]: Unknown operator '{condition.Operator}'. Supported: {string.Join(", ", SupportedOperators.Keys)}";
                return false;
            }

            if (condition.Logic != null && condition.Logic.ToLowerInvariant() is not ("and" or "or"))
            {
                error = $"Condition [{i}]: 'logic' must be 'and' or 'or', got '{condition.Logic}'";
                return false;
            }

            // Validate operator-specific value requirements
            var op = condition.Operator.ToLowerInvariant();
            if (op is "between")
            {
                var values = ExtractArray(condition.Value);
                if (values == null || values.Count != 2)
                {
                    error = $"Condition [{i}]: 'between' operator requires a 2-element array [min, max]";
                    return false;
                }
            }

            if (op is "in" or "not_in")
            {
                var values = ExtractArray(condition.Value);
                if (values == null || values.Count == 0)
                {
                    error = $"Condition [{i}]: '{op}' operator requires a non-empty array value";
                    return false;
                }
            }

            if (op is "matches")
            {
                try
                {
                    _ = new Regex(condition.Value?.ToString() ?? "");
                }
                catch (RegexParseException ex)
                {
                    error = $"Condition [{i}]: Invalid regex pattern: {ex.Message}";
                    return false;
                }
            }
        }

        return true;
    }

    #region Private - Single Condition Evaluation

    /// <summary>
    /// Evaluates a single condition against the context.
    /// Returns (passed, human-readable description).
    /// </summary>
    private (bool passed, string description) EvaluateSingle(PolicyCondition condition, ConditionContext context)
    {
        var fieldValue = context.GetAttribute(condition.Field);
        var description = $"{condition.Field} {condition.Operator} {FormatValue(condition.Value)}";

        // Null attribute -> condition not satisfied (field not present in context)
        if (fieldValue == null)
        {
            return (false, $"{description} [attribute not found]");
        }

        try
        {
            var op = condition.Operator.ToLowerInvariant();
            var passed = op switch
            {
                "eq" => EvaluateEquality(fieldValue, condition.Value, equal: true),
                "neq" => EvaluateEquality(fieldValue, condition.Value, equal: false),
                "gt" => EvaluateComparison(fieldValue, condition.Value) > 0,
                "gte" => EvaluateComparison(fieldValue, condition.Value) >= 0,
                "lt" => EvaluateComparison(fieldValue, condition.Value) < 0,
                "lte" => EvaluateComparison(fieldValue, condition.Value) <= 0,
                "in" => EvaluateIn(fieldValue, condition.Value, negate: false),
                "not_in" => EvaluateIn(fieldValue, condition.Value, negate: true),
                "contains" => EvaluateContains(fieldValue, condition.Value),
                "starts_with" => EvaluateStartsWith(fieldValue, condition.Value),
                "ends_with" => EvaluateEndsWith(fieldValue, condition.Value),
                "matches" => EvaluateMatches(fieldValue, condition.Value),
                "between" => EvaluateBetween(fieldValue, condition.Value),
                "ip_range" => EvaluateIpRange(fieldValue, condition.Value),
                _ => false
            };

            return (passed, description);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Condition evaluation error for {Field} {Operator}: {Error}",
                condition.Field, condition.Operator, ex.Message);
            return (false, $"{description} [error: {ex.Message}]");
        }
    }

    #endregion

    #region Private - Operator Implementations

    private static bool EvaluateEquality(object fieldValue, object conditionValue, bool equal)
    {
        var fieldStr = NormalizeToString(fieldValue);
        var condStr = NormalizeToString(conditionValue);

        // Try boolean comparison
        if (bool.TryParse(fieldStr, out var fieldBool) && bool.TryParse(condStr, out var condBool))
        {
            return equal ? fieldBool == condBool : fieldBool != condBool;
        }

        // Try numeric comparison
        if (TryParseDouble(fieldStr, out var fieldNum) && TryParseDouble(condStr, out var condNum))
        {
            return equal ? Math.Abs(fieldNum - condNum) < 0.0001 : Math.Abs(fieldNum - condNum) >= 0.0001;
        }

        // String comparison (case-insensitive)
        var result = string.Equals(fieldStr, condStr, StringComparison.OrdinalIgnoreCase);
        return equal ? result : !result;
    }

    private static int EvaluateComparison(object fieldValue, object conditionValue)
    {
        var fieldStr = NormalizeToString(fieldValue);
        var condStr = NormalizeToString(conditionValue);

        // Try DateTime comparison
        if (DateTime.TryParse(fieldStr, out var fieldDate) && DateTime.TryParse(condStr, out var condDate))
        {
            return fieldDate.CompareTo(condDate);
        }

        // Numeric comparison
        if (TryParseDouble(fieldStr, out var fieldNum) && TryParseDouble(condStr, out var condNum))
        {
            return fieldNum.CompareTo(condNum);
        }

        throw new InvalidOperationException(
            $"Cannot compare values: '{fieldStr}' and '{condStr}' are not both numeric or DateTime");
    }

    private static bool EvaluateIn(object fieldValue, object conditionValue, bool negate)
    {
        var values = ExtractArray(conditionValue);
        if (values == null || values.Count == 0)
            return negate; // Empty list: not_in = true, in = false

        var fieldStr = NormalizeToString(fieldValue);

        var found = values.Any(v =>
            string.Equals(NormalizeToString(v), fieldStr, StringComparison.OrdinalIgnoreCase));

        return negate ? !found : found;
    }

    private static bool EvaluateContains(object fieldValue, object conditionValue)
    {
        var fieldStr = NormalizeToString(fieldValue);
        var condStr = NormalizeToString(conditionValue);

        return fieldStr.Contains(condStr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateStartsWith(object fieldValue, object conditionValue)
    {
        var fieldStr = NormalizeToString(fieldValue);
        var condStr = NormalizeToString(conditionValue);

        return fieldStr.StartsWith(condStr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateEndsWith(object fieldValue, object conditionValue)
    {
        var fieldStr = NormalizeToString(fieldValue);
        var condStr = NormalizeToString(conditionValue);

        return fieldStr.EndsWith(condStr, StringComparison.OrdinalIgnoreCase);
    }

    private bool EvaluateMatches(object fieldValue, object conditionValue)
    {
        var fieldStr = NormalizeToString(fieldValue);
        var pattern = NormalizeToString(conditionValue);

        try
        {
            // Timeout to prevent ReDoS
            return Regex.IsMatch(fieldStr, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.LogWarning("Regex match timed out for pattern: {Pattern}", pattern);
            return false;
        }
        catch (RegexParseException ex)
        {
            _logger.LogWarning(ex, "Invalid regex pattern: {Pattern}", pattern);
            return false;
        }
    }

    private static bool EvaluateBetween(object fieldValue, object conditionValue)
    {
        var values = ExtractArray(conditionValue);
        if (values == null || values.Count != 2)
            throw new InvalidOperationException("'between' operator requires a 2-element array [min, max]");

        var fieldStr = NormalizeToString(fieldValue);
        var minStr = NormalizeToString(values[0]);
        var maxStr = NormalizeToString(values[1]);

        // Try DateTime
        if (DateTime.TryParse(fieldStr, out var fieldDate) &&
            DateTime.TryParse(minStr, out var minDate) &&
            DateTime.TryParse(maxStr, out var maxDate))
        {
            return fieldDate >= minDate && fieldDate <= maxDate;
        }

        // Numeric
        if (TryParseDouble(fieldStr, out var fieldNum) &&
            TryParseDouble(minStr, out var minNum) &&
            TryParseDouble(maxStr, out var maxNum))
        {
            return fieldNum >= minNum && fieldNum <= maxNum;
        }

        throw new InvalidOperationException(
            $"Cannot evaluate 'between': values must be numeric or DateTime");
    }

    private static bool EvaluateIpRange(object fieldValue, object conditionValue)
    {
        var ipStr = NormalizeToString(fieldValue);
        var cidr = NormalizeToString(conditionValue);

        if (!IPAddress.TryParse(ipStr, out var ipAddress))
            throw new InvalidOperationException($"Invalid IP address: '{ipStr}'");

        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var networkAddress) || !int.TryParse(parts[1], out var prefixLength))
            throw new InvalidOperationException($"Invalid CIDR notation: '{cidr}'. Expected format: '192.168.1.0/24'");

        return IsInSubnet(ipAddress, networkAddress, prefixLength);
    }

    #endregion

    #region Private - Logic Combining

    /// <summary>
    /// Combines individual condition results using the Logic operators (AND/OR).
    /// Processing: conditions are evaluated left-to-right. The Logic property on condition[i]
    /// determines how condition[i]'s result combines with condition[i+1]'s result.
    /// OR has lower precedence: groups of AND-connected conditions are evaluated first,
    /// then OR'd together.
    /// </summary>
    private static bool CombineResults(List<PolicyCondition> conditions, List<(bool passed, string description)> evaluations)
    {
        if (evaluations.Count == 0)
            return true;

        if (evaluations.Count == 1)
            return evaluations[0].passed;

        // Split into OR-separated groups, each group is AND-connected
        var orGroups = new List<List<bool>>();
        var currentGroup = new List<bool> { evaluations[0].passed };

        for (var i = 0; i < conditions.Count - 1; i++)
        {
            var logic = (conditions[i].Logic ?? "and").ToLowerInvariant();

            if (logic == "or")
            {
                // Close current AND-group, start new one
                orGroups.Add(currentGroup);
                currentGroup = new List<bool> { evaluations[i + 1].passed };
            }
            else
            {
                // Continue AND-group
                currentGroup.Add(evaluations[i + 1].passed);
            }
        }

        // Don't forget the last group
        orGroups.Add(currentGroup);

        // Each AND-group must have ALL true, then OR across groups
        return orGroups.Any(group => group.All(b => b));
    }

    #endregion

    #region Private - Helpers

    /// <summary>
    /// Normalizes any value to its string representation.
    /// Handles JsonElement (from System.Text.Json deserialization), primitives, etc.
    /// </summary>
    private static string NormalizeToString(object? value)
    {
        if (value == null)
            return string.Empty;

        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString() ?? string.Empty,
                JsonValueKind.Number => jsonElement.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => jsonElement.GetRawText()
            };
        }

        if (value is JsonNode jsonNode)
        {
            return jsonNode.ToString();
        }

        return value.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Extracts an array from a value that might be a JsonElement array, a List, or other collection.
    /// </summary>
    private static List<object>? ExtractArray(object? value)
    {
        if (value == null)
            return null;

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                return jsonElement.EnumerateArray().Select(e => (object)e).ToList();
            }

            // Single value wrapped in a list
            return new List<object> { value };
        }

        if (value is JsonArray jsonArray)
        {
            return jsonArray.Select(n => (object)(n ?? "")).ToList();
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            return enumerable.Cast<object>().ToList();
        }

        // Single value - wrap
        return new List<object> { value };
    }

    private static bool TryParseDouble(string s, out double result)
    {
        return double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Checks if an IP address falls within a subnet defined by network address and prefix length.
    /// Supports both IPv4 and IPv6.
    /// </summary>
    private static bool IsInSubnet(IPAddress address, IPAddress networkAddress, int prefixLength)
    {
        var addressBytes = address.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();

        if (addressBytes.Length != networkBytes.Length)
            return false; // IPv4 vs IPv6 mismatch

        var totalBits = addressBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > totalBits)
            return false;

        // Compare bit by bit up to prefix length
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        // Compare full bytes
        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
                return false;
        }

        // Compare remaining bits in the partial byte
        if (remainingBits > 0 && fullBytes < addressBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((addressBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }

    private static string FormatValue(object? value)
    {
        if (value == null)
            return "null";

        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String
                ? $"'{je.GetString()}'"
                : je.GetRawText();
        }

        return value is string s ? $"'{s}'" : value.ToString() ?? "null";
    }

    #endregion
}
