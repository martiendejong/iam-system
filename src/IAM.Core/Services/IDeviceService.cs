using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IDeviceService
{
    Task<DeviceResult> RegisterDeviceAsync(RegisterDeviceRequest request);
    Task<Device?> GetDeviceAsync(Guid id);
    Task<Device?> GetDeviceByDeviceIdAsync(string deviceId);
    Task<IEnumerable<Device>> GetAllDevicesAsync();
    Task<IEnumerable<Device>> GetDevicesByTenantAsync(Guid tenantId);
    Task<IEnumerable<Device>> GetDevicesByTypeAsync(string deviceType, Guid? tenantId = null);
    Task<DeviceResult> UpdateDeviceAsync(Guid id, UpdateDeviceRequest request);
    Task<bool> DeactivateDeviceAsync(Guid id);
    Task<bool> UpdateDeviceStatusAsync(string deviceId, bool isOnline, string? ipAddress = null);
    Task<DeviceStatistics> GetStatisticsAsync(Guid? tenantId = null);
}

public class RegisterDeviceRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string AuthenticationMethod { get; set; } = "certificate";
    public Guid TenantId { get; set; }
    public string ResourcePath { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
    public string? Metadata { get; set; }
    public List<string>? Tags { get; set; }
    public Guid? ProvisionedByUserId { get; set; }
}

public class UpdateDeviceRequest
{
    public string? Name { get; set; }
    public string? DeviceType { get; set; }
    public string? ResourcePath { get; set; }
    public List<string>? Permissions { get; set; }
    public string? Metadata { get; set; }
    public List<string>? Tags { get; set; }
    public bool? IsActive { get; set; }
}

public class DeviceResult
{
    public bool Success { get; set; }
    public Device? Device { get; set; }
    public string? SharedSecret { get; set; } // Only returned once during registration for HMAC devices
    public string? Error { get; set; }
}

public class DeviceStatistics
{
    public int TotalDevices { get; set; }
    public int ActiveDevices { get; set; }
    public int OnlineDevices { get; set; }
    public int CertificateDevices { get; set; }
    public int HmacDevices { get; set; }
    public Dictionary<string, int> DevicesByType { get; set; } = new();
}
