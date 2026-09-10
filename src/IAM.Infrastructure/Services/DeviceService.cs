using System.Security.Cryptography;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class DeviceService : IDeviceService
{
    private readonly IAMDbContext _context;

    public DeviceService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<DeviceResult> RegisterDeviceAsync(RegisterDeviceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return new DeviceResult { Success = false, Error = "Device ID is required" };
        }

        if (string.IsNullOrWhiteSpace(request.ResourcePath))
        {
            return new DeviceResult { Success = false, Error = "Resource path is required" };
        }

        // Check for duplicate device ID
        if (await _context.Devices.AnyAsync(d => d.DeviceId == request.DeviceId))
        {
            return new DeviceResult { Success = false, Error = "Device ID already registered" };
        }

        // Verify tenant exists
        var tenant = await _context.Tenants.FindAsync(request.TenantId);
        if (tenant == null)
        {
            return new DeviceResult { Success = false, Error = "Tenant not found" };
        }

        string? sharedSecret = null;
        string? sharedSecretHash = null;

        // Generate shared secret for HMAC devices
        if (request.AuthenticationMethod == "hmac")
        {
            sharedSecret = GenerateSharedSecret();
            sharedSecretHash = HashSecret(sharedSecret);
        }

        var device = new Device
        {
            DeviceId = request.DeviceId,
            Name = request.Name,
            DeviceType = request.DeviceType,
            AuthenticationMethod = request.AuthenticationMethod,
            TenantId = request.TenantId,
            ResourcePath = request.ResourcePath,
            Permissions = JsonSerializer.Serialize(request.Permissions),
            SharedSecretHash = sharedSecretHash,
            Metadata = request.Metadata,
            Tags = request.Tags != null ? JsonSerializer.Serialize(request.Tags) : null,
            IsActive = true,
            IsProvisioned = true,
            ProvisionedAt = DateTime.UtcNow,
            ProvisionedByUserId = request.ProvisionedByUserId
        };

        _context.Devices.Add(device);
        await _context.SaveChangesAsync();

        return new DeviceResult
        {
            Success = true,
            Device = device,
            SharedSecret = sharedSecret // Only returned once for HMAC devices
        };
    }

    public async Task<Device?> GetDeviceAsync(Guid id)
    {
        return await _context.Devices
            .Include(d => d.Tenant)
            .Include(d => d.Certificates.Where(c => c.Status == "Active"))
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<Device?> GetDeviceByDeviceIdAsync(string deviceId)
    {
        return await _context.Devices
            .Include(d => d.Tenant)
            .Include(d => d.Certificates.Where(c => c.Status == "Active"))
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId);
    }

    public async Task<IEnumerable<Device>> GetAllDevicesAsync()
    {
        return await _context.Devices
            .Include(d => d.Tenant)
            .OrderBy(d => d.DeviceType)
            .ThenBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<Device>> GetDevicesByTenantAsync(Guid tenantId)
    {
        return await _context.Devices
            .Include(d => d.Tenant)
            .Where(d => d.TenantId == tenantId && d.IsActive)
            .OrderBy(d => d.DeviceType)
            .ThenBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<Device>> GetDevicesByTypeAsync(string deviceType, Guid? tenantId = null)
    {
        var query = _context.Devices
            .Include(d => d.Tenant)
            .Where(d => d.DeviceType == deviceType && d.IsActive);

        if (tenantId.HasValue)
        {
            query = query.Where(d => d.TenantId == tenantId.Value);
        }

        return await query
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<DeviceResult> UpdateDeviceAsync(Guid id, UpdateDeviceRequest request)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null)
        {
            return new DeviceResult { Success = false, Error = "Device not found" };
        }

        if (request.Name != null) device.Name = request.Name;
        if (request.DeviceType != null) device.DeviceType = request.DeviceType;
        if (request.ResourcePath != null) device.ResourcePath = request.ResourcePath;
        if (request.Permissions != null) device.Permissions = JsonSerializer.Serialize(request.Permissions);
        if (request.Metadata != null) device.Metadata = request.Metadata;
        if (request.Tags != null) device.Tags = JsonSerializer.Serialize(request.Tags);
        if (request.IsActive.HasValue) device.IsActive = request.IsActive.Value;

        device.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return new DeviceResult { Success = true, Device = device };
    }

    public async Task<bool> DeactivateDeviceAsync(Guid id)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null) return false;

        device.IsActive = false;
        device.UpdatedAt = DateTime.UtcNow;

        // Revoke all active certificates
        var certs = await _context.DeviceCertificates
            .Where(c => c.DeviceId == id && c.Status == "Active")
            .ToListAsync();

        foreach (var cert in certs)
        {
            cert.Status = "Revoked";
            cert.RevocationReason = "Device deactivated";
            cert.RevokedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateDeviceStatusAsync(string deviceId, bool isOnline, string? ipAddress = null)
    {
        var device = await _context.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null) return false;

        device.IsOnline = isOnline;
        device.LastSeenAt = DateTime.UtcNow;
        if (ipAddress != null) device.LastIpAddress = ipAddress;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<DeviceStatistics> GetStatisticsAsync(Guid? tenantId = null)
    {
        var query = _context.Devices.AsQueryable();
        if (tenantId.HasValue)
        {
            query = query.Where(d => d.TenantId == tenantId.Value);
        }

        var devices = await query.ToListAsync();

        return new DeviceStatistics
        {
            TotalDevices = devices.Count,
            ActiveDevices = devices.Count(d => d.IsActive),
            OnlineDevices = devices.Count(d => d.IsOnline),
            CertificateDevices = devices.Count(d => d.AuthenticationMethod == "certificate"),
            HmacDevices = devices.Count(d => d.AuthenticationMethod == "hmac"),
            DevicesByType = devices
                .GroupBy(d => d.DeviceType)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private static string GenerateSharedSecret()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string HashSecret(string secret)
    {
        return BCrypt.Net.BCrypt.HashPassword(secret, workFactor: 12);
    }
}
