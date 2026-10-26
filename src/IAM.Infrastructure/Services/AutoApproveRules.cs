using System.Text.Json;
using IAM.Core.Entities;

namespace IAM.Infrastructure.Services;

/// <summary>
/// One entry of a workflow template's AutoApproveRules JSON (task 5152). A rule matches a request when every condition
/// it sets matches; a rule that sets no narrowing condition would match every request of the template.
/// </summary>
internal sealed class AutoApproveRule
{
    public string? ResourceType { get; set; }
    public Guid? RoleId { get; set; }
    public string? MaxPriority { get; set; }

    /// <summary>
    /// True when the rule limits what it approves: a specific role, or a maximum priority below Critical. A resource type
    /// alone does not count (the template already is for one resource type, so it still matches every request), and
    /// neither does an unparseable or Critical (= highest) priority.
    /// </summary>
    public bool IsNarrowing()
    {
        if (RoleId.HasValue && RoleId.Value != Guid.Empty)
            return true;

        return !string.IsNullOrEmpty(MaxPriority)
            && Enum.TryParse<AccessRequestPriority>(MaxPriority, true, out var max)
            && Enum.IsDefined(max)
            && max < AccessRequestPriority.Critical;
    }
}

/// <summary>Parses and checks auto-approve rules; used on save and again when a request is matched.</summary>
internal static class AutoApproveRules
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <exception cref="JsonException">The text is not a JSON array of rule objects.</exception>
    public static List<AutoApproveRule> Parse(string rulesJson) =>
        JsonSerializer.Deserialize<List<AutoApproveRule>>(rulesJson, Options) ?? new List<AutoApproveRule>();

    /// <returns>An error message when the rules cannot be saved, otherwise null (no rules at all is fine).</returns>
    public static string? Validate(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return null;

        List<AutoApproveRule> rules;
        try
        {
            rules = Parse(rulesJson);
        }
        catch (JsonException)
        {
            return "Auto-approve rules must be a JSON array of rule objects.";
        }

        if (rules.Any(r => r == null))
            return "Auto-approve rules must be a JSON array of rule objects.";

        if (rules.Any(r => !r.IsNarrowing()))
            return "Every auto-approve rule needs at least one narrowing condition (RoleId, or a MaxPriority below Critical); a rule without one would approve every request.";

        return null;
    }
}
