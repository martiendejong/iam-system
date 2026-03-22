using System.Text.Json;

namespace IAM.Core.Entities;

/// <summary>
/// Represents an access control policy with spatial inheritance support.
/// Policies can be defined at any level of the hierarchy and automatically apply to descendants.
/// </summary>
public class Policy
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable policy name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of what this policy controls
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Tenant (scope) where this policy is defined.
    /// Can be Organization, Building, Floor, Room, or Device.
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Defines how this policy applies to child tenants in the hierarchy.
    /// </summary>
    public InheritanceScope InheritanceScope { get; set; } = InheritanceScope.Self;

    /// <summary>
    /// If this policy was inherited from a parent, this references the source policy.
    /// Null if this is an original (non-inherited) policy.
    /// </summary>
    public Guid? InheritedFromPolicyId { get; set; }
    public Policy? InheritedFromPolicy { get; set; }

    /// <summary>
    /// Role required to have this access. Null = applies to all authenticated users.
    /// </summary>
    public Guid? RoleId { get; set; }
    public Role? Role { get; set; }

    /// <summary>
    /// Specific user this policy applies to. Null = applies based on role.
    /// </summary>
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Resource this policy grants access to.
    /// Format: "Resource:Action" (e.g., "Door:Unlock", "Camera:View", "HVAC:Control")
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// Action allowed on the resource (View, Create, Update, Delete, Execute, Control, etc.)
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Effect of this policy: Allow or Deny.
    /// Deny policies override Allow policies (explicit deny wins).
    /// </summary>
    public PolicyEffect Effect { get; set; } = PolicyEffect.Allow;

    /// <summary>
    /// Priority for conflict resolution. Higher priority wins.
    /// Default: 0. Inherited policies have priority of parent - 1.
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Time-based constraints (optional).
    /// JSON: { "start_time": "09:00", "end_time": "17:00", "days_of_week": [1,2,3,4,5], "timezone": "UTC" }
    /// </summary>
    public string? TimeConstraints { get; set; }

    /// <summary>
    /// Optional schedule template for recurring time patterns.
    /// If specified, overrides simple TimeConstraints with advanced scheduling.
    /// </summary>
    public Guid? ScheduleTemplateId { get; set; }
    public ScheduleTemplate? ScheduleTemplate { get; set; }

    /// <summary>
    /// Conditional constraints (optional).
    /// JSON: { "ip_whitelist": ["10.0.0.0/8"], "device_health": "trusted", "location": "geofence:headquarters" }
    /// </summary>
    public string? Conditions { get; set; }

    /// <summary>
    /// Policy expiration date (auto-revoke after this time).
    /// Null = no expiration.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    // Navigation properties
    public ICollection<Policy> InheritedPolicies { get; set; } = new List<Policy>();

    /// <summary>
    /// Parses time constraints from JSON
    /// </summary>
    public TimeConstraint? GetTimeConstraints()
    {
        if (string.IsNullOrWhiteSpace(TimeConstraints))
            return null;

        return JsonSerializer.Deserialize<TimeConstraint>(TimeConstraints);
    }

    /// <summary>
    /// Parses conditional constraints from JSON
    /// </summary>
    public Dictionary<string, object>? GetConditions()
    {
        if (string.IsNullOrWhiteSpace(Conditions))
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object>>(Conditions);
    }
}

/// <summary>
/// Defines how a policy applies to descendant tenants in the hierarchy
/// </summary>
public enum InheritanceScope
{
    /// <summary>
    /// Policy applies only to the tenant where it's defined (no inheritance)
    /// </summary>
    Self = 0,

    /// <summary>
    /// Policy applies to direct children only (1 level down)
    /// </summary>
    Children = 1,

    /// <summary>
    /// Policy applies to all descendants recursively (entire subtree)
    /// </summary>
    Descendants = 2
}

/// <summary>
/// Policy effect: Allow or Deny access
/// </summary>
public enum PolicyEffect
{
    Allow = 0,
    Deny = 1
}

/// <summary>
/// Time-based constraint model
/// </summary>
public class TimeConstraint
{
    /// <summary>
    /// Start time in HH:mm format (24-hour)
    /// </summary>
    public string? StartTime { get; set; }

    /// <summary>
    /// End time in HH:mm format (24-hour)
    /// </summary>
    public string? EndTime { get; set; }

    /// <summary>
    /// Days of week (1=Monday, 7=Sunday). Empty = all days.
    /// </summary>
    public List<int> DaysOfWeek { get; set; } = new();

    /// <summary>
    /// Timezone for time evaluation (IANA timezone)
    /// </summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>
    /// Cron expression for complex recurring schedules (optional, overrides StartTime/EndTime)
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>
    /// Start date (policy not active before this date)
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// End date (policy not active after this date)
    /// </summary>
    public DateTime? EndDate { get; set; }
}
