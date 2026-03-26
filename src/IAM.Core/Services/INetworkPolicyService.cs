using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for IP allowlisting, geo restriction, and geofencing network policies
/// </summary>
public interface INetworkPolicyService
{
    // IP Allowlist CRUD
    Task<List<IpAllowlistEntry>> GetIpAllowlistAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<IpAllowlistEntry> CreateIpAllowlistEntryAsync(Guid tenantId, string cidr, string? description, CancellationToken cancellationToken = default);
    Task<IpAllowlistEntry> UpdateIpAllowlistEntryAsync(Guid id, string cidr, string? description, bool isActive, CancellationToken cancellationToken = default);
    Task DeleteIpAllowlistEntryAsync(Guid id, CancellationToken cancellationToken = default);

    // Geo Restriction CRUD
    Task<GeoRestriction?> GetGeoRestrictionAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<GeoRestriction> UpsertGeoRestrictionAsync(Guid tenantId, string? allowedCountries, string? blockedCountries, bool isActive, CancellationToken cancellationToken = default);
    Task DeleteGeoRestrictionAsync(Guid tenantId, CancellationToken cancellationToken = default);

    // GeoFence CRUD
    Task<List<GeoFence>> GetGeoFencesAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<GeoFence> CreateGeoFenceAsync(Guid tenantId, string name, double latitude, double longitude, double radiusMeters, string? description, CancellationToken cancellationToken = default);
    Task<GeoFence> UpdateGeoFenceAsync(Guid id, string name, double latitude, double longitude, double radiusMeters, string? description, bool isActive, CancellationToken cancellationToken = default);
    Task DeleteGeoFenceAsync(Guid id, CancellationToken cancellationToken = default);

    // Blocked IP Log
    Task<List<BlockedIpLog>> GetBlockedIpLogsAsync(Guid tenantId, int skip = 0, int take = 100, CancellationToken cancellationToken = default);

    // IP Check (validation engine)
    Task<IpCheckResult> CheckIpAsync(Guid tenantId, string ipAddress, double? latitude = null, double? longitude = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of checking an IP against all network policies for a tenant
/// </summary>
public class IpCheckResult
{
    public bool IsAllowed { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string? Country { get; set; }
    public string? City { get; set; }
    public List<string> Reasons { get; set; } = new();
    public bool PassedIpAllowlist { get; set; }
    public bool PassedGeoRestriction { get; set; }
    public bool PassedGeoFence { get; set; }
}
