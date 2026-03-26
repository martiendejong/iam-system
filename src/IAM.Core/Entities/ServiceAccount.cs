using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

/// <summary>
/// Service account type indicating the purpose of the service identity.
/// </summary>
public enum ServiceAccountType
{
    Api = 0,
    Service = 1,
    Worker = 2
}

/// <summary>
/// Represents a service account for API gateway and service mesh authentication.
/// Supports client_credentials grant, token exchange (RFC 8693), and mTLS validation.
/// </summary>
public class ServiceAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// OAuth2 client_id for this service account.
    /// </summary>
    [Required, MaxLength(128)]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// SHA256 hash of the client secret. The raw secret is never stored.
    /// </summary>
    [Required, MaxLength(128)]
    public string ClientSecretHash { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of permission strings granted to this service account.
    /// </summary>
    public string Permissions { get; set; } = "[]";

    /// <summary>
    /// The type of service account: Api, Service, or Worker.
    /// </summary>
    public ServiceAccountType Type { get; set; } = ServiceAccountType.Api;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Certificate thumbprint for mTLS authentication. Null if mTLS is not used.
    /// </summary>
    [MaxLength(128)]
    public string? CertificateThumbprint { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastAuthenticatedAt { get; set; }
}
