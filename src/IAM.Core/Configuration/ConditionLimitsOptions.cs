namespace IAM.Core.Configuration;

/// <summary>
/// Hard limits for ABAC condition evaluation. Applied in one place (ConditionEvaluator)
/// and used by the evaluate/validate endpoints and policy create/update.
/// </summary>
public class ConditionLimitsOptions
{
    public const string SectionName = "ConditionLimits";

    /// <summary>Maximum number of conditions in one request or policy.</summary>
    public int MaxConditions { get; set; } = 50;

    /// <summary>Maximum length of a regex pattern used by the 'matches' operator.</summary>
    public int MaxPatternLength { get; set; } = 256;

    /// <summary>Maximum length of any single string value (condition value or context attribute).</summary>
    public int MaxValueLength { get; set; } = 2048;

    /// <summary>Maximum number of items in an array value (in, not_in, between).</summary>
    public int MaxArrayItems { get; set; } = 200;

    /// <summary>Time budget in milliseconds for a single regex match.</summary>
    public int RegexTimeoutMs { get; set; } = 250;

    /// <summary>Maximum request body size in bytes for the evaluate/validate endpoints.</summary>
    public long MaxRequestBodyBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Checks the key/value condition map stored on a policy against the limits.
    /// Returns an error message, or null when within limits.
    /// </summary>
    public string? CheckPolicyConditions(IDictionary<string, object>? conditions)
    {
        if (conditions == null) return null;
        if (conditions.Count > MaxConditions)
            return $"Too many policy conditions: {conditions.Count} (maximum {MaxConditions})";
        foreach (var (key, value) in conditions)
        {
            if (key.Length > MaxValueLength)
                return $"Policy condition key is longer than {MaxValueLength} characters";
            var text = value?.ToString() ?? "";
            if (text.Length > MaxValueLength)
                return $"Policy condition value for '{(key.Length > 64 ? key[..64] : key)}' is longer than {MaxValueLength} characters";
        }
        return null;
    }
}
