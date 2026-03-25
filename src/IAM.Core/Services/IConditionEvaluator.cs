namespace IAM.Core.Services;

/// <summary>
/// ABAC (Attribute-Based Access Control) condition evaluation engine.
/// Evaluates policy conditions against request context attributes (user, resource, environment, action).
/// </summary>
public interface IConditionEvaluator
{
    /// <summary>
    /// Evaluates conditions from a JSON string against the provided context.
    /// </summary>
    /// <param name="conditionsJson">JSON array of PolicyCondition objects</param>
    /// <param name="context">Request context with user/resource/env/action attributes</param>
    /// <returns>Evaluation result with matched/failed conditions and overall satisfaction</returns>
    ConditionEvaluationResult Evaluate(string conditionsJson, ConditionContext context);

    /// <summary>
    /// Evaluates a list of conditions against the provided context.
    /// </summary>
    /// <param name="conditions">List of PolicyCondition objects</param>
    /// <param name="context">Request context with user/resource/env/action attributes</param>
    /// <returns>Evaluation result with matched/failed conditions and overall satisfaction</returns>
    ConditionEvaluationResult Evaluate(List<PolicyCondition> conditions, ConditionContext context);

    /// <summary>
    /// Validates that a conditions JSON string is syntactically correct and uses supported operators/fields.
    /// </summary>
    /// <param name="conditionsJson">JSON string to validate</param>
    /// <param name="error">Error description if validation fails, null if valid</param>
    /// <returns>True if valid, false otherwise</returns>
    bool ValidateConditions(string conditionsJson, out string? error);
}
