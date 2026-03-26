namespace IAM.Core.Entities;

/// <summary>
/// Defines the specific resources/areas a visitor is granted access to during their visit.
/// Linked to a Visitor with a defined validity window.
/// </summary>
public class VisitorAccessGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The visitor this access grant belongs to
    /// </summary>
    public Guid VisitorId { get; set; }
    public Visitor? Visitor { get; set; }

    /// <summary>
    /// JSON array of resource identifiers the visitor can access
    /// (e.g. room IDs, floor IDs, system names)
    /// </summary>
    public string Resources { get; set; } = "[]";

    /// <summary>
    /// When this access grant becomes valid (UTC)
    /// </summary>
    public DateTime ValidFrom { get; set; }

    /// <summary>
    /// When this access grant expires (UTC)
    /// </summary>
    public DateTime ValidUntil { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Check if this grant is currently valid
    /// </summary>
    public bool IsCurrentlyValid()
    {
        var now = DateTime.UtcNow;
        return now >= ValidFrom && now <= ValidUntil;
    }
}
