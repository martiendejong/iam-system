namespace IAM.Core.Entities;

/// <summary>
/// Represents an IoT device, service, or machine identity in the system.
/// Supports both capable devices (X.509 certificates) and constrained devices (HMAC tokens).
/// </summary>
public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable device identifier (e.g., "acme-hq-floor3-hvac-unit247")
    /// Must be unique within a tenant.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Display name (e.g., "Floor 3 HVAC Unit 247")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Device type category (e.g., "hvac", "sensor", "lighting", "access", "camera", "robot")
    /// Used for grouping and policy templates.
    /// </summary>
    public string DeviceType { get; set; } = string.Empty;

    /// <summary>
    /// Authentication method: "certificate" (X.509 mTLS) or "hmac" (HMAC-SHA256 shared secret)
    /// </summary>
    public string AuthenticationMethod { get; set; } = "certificate";

    /// <summary>
    /// Tenant (location) this device belongs to.
    /// Maps to the hierarchical resource model (org:building:floor:zone).
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Hierarchical resource path for authorization.
    /// Format: "org:sub-org:location:resource-type:resource-id"
    /// Example: "acme:headquarters:floor-3:hvac:unit-247"
    /// </summary>
    public string ResourcePath { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of permission strings granted to this device.
    /// Example: ["acme:headquarters:floor-3:hvac:unit-247:telemetry:write",
    ///           "acme:headquarters:floor-3:hvac:unit-247:commands:read"]
    /// </summary>
    public string Permissions { get; set; } = "[]";

    /// <summary>
    /// HMAC shared secret (for constrained devices using HMAC authentication).
    /// Stored as hashed value. Null for certificate-based devices.
    /// </summary>
    public string? SharedSecretHash { get; set; }

    /// <summary>
    /// Hardware/firmware metadata in JSON format.
    /// Example: {"manufacturer": "Honeywell", "model": "T6 Pro", "firmware": "1.2.3",
    ///           "mac_address": "AA:BB:CC:DD:EE:FF", "ip_address": "192.168.1.42"}
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>
    /// Tags for grouping and filtering (JSON array).
    /// Example: ["hvac", "floor-3", "critical", "maintenance-scheduled"]
    /// </summary>
    public string? Tags { get; set; }

    // Status tracking
    public bool IsActive { get; set; } = true;
    public bool IsOnline { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastAuthenticatedAt { get; set; }
    public string? LastIpAddress { get; set; }

    // Provisioning
    public bool IsProvisioned { get; set; }
    public DateTime? ProvisionedAt { get; set; }
    public Guid? ProvisionedByUserId { get; set; }
    public User? ProvisionedByUser { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<DeviceCertificate> Certificates { get; set; } = new List<DeviceCertificate>();
}
