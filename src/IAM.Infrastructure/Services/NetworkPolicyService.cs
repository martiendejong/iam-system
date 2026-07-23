using System.Net;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class NetworkPolicyService : INetworkPolicyService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<NetworkPolicyService> _logger;

    // Earth radius in meters for Haversine formula
    private const double EarthRadiusMeters = 6_371_000.0;

    public NetworkPolicyService(IAMDbContext context, ILogger<NetworkPolicyService> logger)
    {
        _context = context;
        _logger = logger;
    }

    #region IP Allowlist CRUD

    public async Task<List<IpAllowlistEntry>> GetIpAllowlistAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return await _context.IpAllowlistEntries
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IpAllowlistEntry> CreateIpAllowlistEntryAsync(Guid tenantId, string cidr, string? description, CancellationToken cancellationToken = default)
    {
        // Validate CIDR
        if (!IsValidCidr(cidr))
            throw new ArgumentException($"Invalid CIDR notation: {cidr}");

        var entry = new IpAllowlistEntry
        {
            TenantId = tenantId,
            Cidr = NormalizeCidr(cidr),
            Description = description,
            IsActive = true
        };

        _context.IpAllowlistEntries.Add(entry);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created IP allowlist entry {Cidr} for tenant {TenantId}", cidr, tenantId);
        return entry;
    }

    public async Task<IpAllowlistEntry> UpdateIpAllowlistEntryAsync(Guid id, string cidr, string? description, bool isActive, CancellationToken cancellationToken = default)
    {
        var entry = await _context.IpAllowlistEntries.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new ArgumentException($"IP allowlist entry {id} not found");

        if (!IsValidCidr(cidr))
            throw new ArgumentException($"Invalid CIDR notation: {cidr}");

        entry.Cidr = NormalizeCidr(cidr);
        entry.Description = description;
        entry.IsActive = isActive;
        entry.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task DeleteIpAllowlistEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entry = await _context.IpAllowlistEntries.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new ArgumentException($"IP allowlist entry {id} not found");

        _context.IpAllowlistEntries.Remove(entry);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Deleted IP allowlist entry {Id} ({Cidr})", id, entry.Cidr);
    }

    #endregion

    #region Geo Restriction CRUD

    public async Task<GeoRestriction?> GetGeoRestrictionAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return await _context.GeoRestrictions
            .FirstOrDefaultAsync(r => r.TenantId == tenantId, cancellationToken);
    }

    public async Task<GeoRestriction> UpsertGeoRestrictionAsync(Guid tenantId, string? allowedCountries, string? blockedCountries, bool isActive, CancellationToken cancellationToken = default)
    {
        // Validate JSON arrays
        if (allowedCountries != null) ValidateCountryCodesJson(allowedCountries);
        if (blockedCountries != null) ValidateCountryCodesJson(blockedCountries);

        var existing = await _context.GeoRestrictions
            .FirstOrDefaultAsync(r => r.TenantId == tenantId, cancellationToken);

        if (existing != null)
        {
            existing.AllowedCountries = allowedCountries;
            existing.BlockedCountries = blockedCountries;
            existing.IsActive = isActive;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new GeoRestriction
            {
                TenantId = tenantId,
                AllowedCountries = allowedCountries,
                BlockedCountries = blockedCountries,
                IsActive = isActive
            };
            _context.GeoRestrictions.Add(existing);
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Upserted geo restriction for tenant {TenantId}", tenantId);
        return existing;
    }

    public async Task DeleteGeoRestrictionAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var restriction = await _context.GeoRestrictions
            .FirstOrDefaultAsync(r => r.TenantId == tenantId, cancellationToken)
            ?? throw new ArgumentException($"Geo restriction for tenant {tenantId} not found");

        _context.GeoRestrictions.Remove(restriction);
        await _context.SaveChangesAsync(cancellationToken);
    }

    #endregion

    #region GeoFence CRUD

    public async Task<List<GeoFence>> GetGeoFencesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return await _context.GeoFences
            .Where(f => f.TenantId == tenantId)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<GeoFence> CreateGeoFenceAsync(Guid tenantId, string name, double latitude, double longitude, double radiusMeters, string? description, CancellationToken cancellationToken = default)
    {
        ValidateCoordinates(latitude, longitude);
        if (radiusMeters <= 0) throw new ArgumentException("Radius must be greater than 0");

        var fence = new GeoFence
        {
            TenantId = tenantId,
            Name = name,
            Latitude = latitude,
            Longitude = longitude,
            RadiusMeters = radiusMeters,
            Description = description,
            IsActive = true
        };

        _context.GeoFences.Add(fence);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created geofence {Name} at ({Lat},{Lon}) r={Radius}m for tenant {TenantId}",
            name, latitude, longitude, radiusMeters, tenantId);
        return fence;
    }

    public async Task<GeoFence> UpdateGeoFenceAsync(Guid id, string name, double latitude, double longitude, double radiusMeters, string? description, bool isActive, CancellationToken cancellationToken = default)
    {
        ValidateCoordinates(latitude, longitude);
        if (radiusMeters <= 0) throw new ArgumentException("Radius must be greater than 0");

        var fence = await _context.GeoFences.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new ArgumentException($"GeoFence {id} not found");

        fence.Name = name;
        fence.Latitude = latitude;
        fence.Longitude = longitude;
        fence.RadiusMeters = radiusMeters;
        fence.Description = description;
        fence.IsActive = isActive;

        await _context.SaveChangesAsync(cancellationToken);
        return fence;
    }

    public async Task DeleteGeoFenceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var fence = await _context.GeoFences.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new ArgumentException($"GeoFence {id} not found");

        _context.GeoFences.Remove(fence);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Deleted geofence {Id} ({Name})", id, fence.Name);
    }

    #endregion

    #region Blocked IP Log

    public async Task<List<BlockedIpLog>> GetBlockedIpLogsAsync(Guid tenantId, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        return await _context.BlockedIpLogs
            .Where(l => l.TenantId == tenantId)
            .OrderByDescending(l => l.BlockedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    #endregion

    #region IP Check Engine

    public async Task<IpCheckResult> CheckIpAsync(Guid tenantId, string ipAddress, double? latitude = null, double? longitude = null, CancellationToken cancellationToken = default)
    {
        var result = new IpCheckResult
        {
            IpAddress = ipAddress,
            IsAllowed = true,
            PassedIpAllowlist = true,
            PassedGeoRestriction = true,
            PassedGeoFence = true
        };

        // Resolve geo info from IP (simplified - in production use MaxMind GeoIP2 or similar)
        var geoInfo = ResolveGeoInfo(ipAddress);
        result.Country = geoInfo.Country;
        result.City = geoInfo.City;

        // 1. Check IP Allowlist
        var allowlistEntries = await _context.IpAllowlistEntries
            .Where(e => e.TenantId == tenantId && e.IsActive)
            .ToListAsync(cancellationToken);

        if (allowlistEntries.Any())
        {
            var ipAllowed = allowlistEntries.Any(e => IsIpInCidr(ipAddress, e.Cidr));
            if (!ipAllowed)
            {
                result.IsAllowed = false;
                result.PassedIpAllowlist = false;
                result.Reasons.Add($"IP {ipAddress} is not in the tenant IP allowlist");
            }
        }

        // 2. Check Geo Restriction
        var geoRestriction = await _context.GeoRestrictions
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IsActive, cancellationToken);

        if (geoRestriction != null && result.Country != null)
        {
            if (geoRestriction.AllowedCountries != null)
            {
                var allowed = JsonSerializer.Deserialize<List<string>>(geoRestriction.AllowedCountries) ?? new();
                if (!allowed.Contains(result.Country, StringComparer.OrdinalIgnoreCase))
                {
                    result.IsAllowed = false;
                    result.PassedGeoRestriction = false;
                    result.Reasons.Add($"Country {result.Country} is not in the allowed countries list");
                }
            }
            else if (geoRestriction.BlockedCountries != null)
            {
                var blocked = JsonSerializer.Deserialize<List<string>>(geoRestriction.BlockedCountries) ?? new();
                if (blocked.Contains(result.Country, StringComparer.OrdinalIgnoreCase))
                {
                    result.IsAllowed = false;
                    result.PassedGeoRestriction = false;
                    result.Reasons.Add($"Country {result.Country} is in the blocked countries list");
                }
            }
        }

        // 3. Check GeoFences (if coordinates provided)
        if (latitude.HasValue && longitude.HasValue)
        {
            var fences = await _context.GeoFences
                .Where(f => f.TenantId == tenantId && f.IsActive)
                .ToListAsync(cancellationToken);

            if (fences.Any())
            {
                var insideAnyFence = fences.Any(f =>
                    HaversineDistance(latitude.Value, longitude.Value, f.Latitude, f.Longitude) <= f.RadiusMeters);

                if (!insideAnyFence)
                {
                    result.IsAllowed = false;
                    result.PassedGeoFence = false;
                    result.Reasons.Add("Location is outside all configured geofences");
                }
            }
        }

        // Log if blocked
        if (!result.IsAllowed)
        {
            var log = new BlockedIpLog
            {
                TenantId = tenantId,
                IpAddress = ipAddress,
                Reason = string.Join("; ", result.Reasons),
                Country = result.Country,
                City = result.City
            };
            _context.BlockedIpLogs.Add(log);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogWarning("Blocked IP {IpAddress} for tenant {TenantId}: {Reasons}",
                ipAddress, tenantId, string.Join("; ", result.Reasons));
        }

        return result;
    }

    #endregion

    #region CIDR Matching

    /// <summary>
    /// Check if an IP address falls within a CIDR range
    /// </summary>
    private static bool IsIpInCidr(string ipAddress, string cidr)
    {
        try
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
                return false;

            var parts = cidr.Split('/');
            if (!IPAddress.TryParse(parts[0], out var networkAddress))
                return false;

            int prefixLength = parts.Length > 1 ? int.Parse(parts[1]) : 32;

            var ipBytes = ip.MapToIPv4().GetAddressBytes();
            var networkBytes = networkAddress.MapToIPv4().GetAddressBytes();

            if (ipBytes.Length != networkBytes.Length)
                return false;

            // Create mask
            int totalBits = ipBytes.Length * 8;
            for (int i = 0; i < ipBytes.Length; i++)
            {
                int bitsToCheck = Math.Min(8, Math.Max(0, prefixLength - (i * 8)));
                byte mask = (byte)(0xFF << (8 - bitsToCheck));

                if ((ipBytes[i] & mask) != (networkBytes[i] & mask))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validate that a string is valid CIDR notation
    /// </summary>
    private static bool IsValidCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length > 2) return false;

        if (!IPAddress.TryParse(parts[0], out _))
            return false;

        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], out var prefix))
                return false;
            if (prefix < 0 || prefix > 128) // Allow IPv6 prefix lengths too
                return false;
        }

        return true;
    }

    /// <summary>
    /// Normalize CIDR: add /32 for single IPv4 addresses
    /// </summary>
    private static string NormalizeCidr(string cidr)
    {
        if (!cidr.Contains('/'))
        {
            if (IPAddress.TryParse(cidr, out var ip))
            {
                // Check if it's IPv4 mapped or pure IPv4
                return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    ? $"{cidr}/32"
                    : $"{cidr}/128";
            }
        }
        return cidr;
    }

    #endregion

    #region Haversine Distance

    /// <summary>
    /// Calculate the distance in meters between two coordinates using the Haversine formula
    /// </summary>
    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    #endregion

    #region Geo Lookup (Placeholder)

    /// <summary>
    /// Resolve geographic info from an IP address.
    /// In production, integrate with MaxMind GeoIP2 or similar service.
    /// </summary>
    private static GeoInfo ResolveGeoInfo(string ipAddress)
    {
        // Placeholder - in production, use MaxMind GeoIP2 database
        // Example: var response = geoIp2Reader.City(ipAddress);
        // return new GeoInfo { Country = response.Country.IsoCode, City = response.City.Name };

        // For private/local IPs, return unknown
        if (IPAddress.TryParse(ipAddress, out var ip))
        {
            if (IPAddress.IsLoopback(ip) || IsPrivateIp(ip))
            {
                return new GeoInfo { Country = null, City = null };
            }
        }

        return new GeoInfo { Country = null, City = null };
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        var bytes = ip.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private class GeoInfo
    {
        public string? Country { get; set; }
        public string? City { get; set; }
    }

    #endregion

    #region Validation Helpers

    private static void ValidateCountryCodesJson(string json)
    {
        try
        {
            var codes = JsonSerializer.Deserialize<List<string>>(json);
            if (codes == null) throw new ArgumentException("Country codes must be a JSON array of strings");
            foreach (var code in codes)
            {
                if (code.Length != 2)
                    throw new ArgumentException($"Invalid ISO 3166-1 alpha-2 country code: {code}");
            }
        }
        catch (JsonException)
        {
            throw new ArgumentException("Country codes must be a valid JSON array of strings");
        }
    }

    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (latitude < -90 || latitude > 90)
            throw new ArgumentException("Latitude must be between -90 and 90");
        if (longitude < -180 || longitude > 180)
            throw new ArgumentException("Longitude must be between -180 and 180");
    }

    #endregion
}
