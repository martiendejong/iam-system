using IAM.Core.Entities;

namespace IAM.Core.Interfaces;

/// <summary>
/// Service for managing IoT devices
/// </summary>
public interface IIoTDeviceService
{
    Task<IoTDevice?> GetByIdAsync(Guid id, Guid tenantId);
    Task<IoTDevice?> GetByDeviceIdAsync(string deviceId, Guid tenantId);
    Task<IEnumerable<IoTDevice>> GetAllAsync(Guid tenantId, bool includeInactive = false);
    Task<IEnumerable<IoTDevice>> GetByRoomAsync(Guid roomId, Guid tenantId);
    Task<IEnumerable<IoTDevice>> GetByTypeAsync(DeviceType type, Guid tenantId);
    Task<IEnumerable<IoTDevice>> GetByStatusAsync(DeviceStatus status, Guid tenantId);
    Task<IEnumerable<IoTDevice>> GetStreamingDevicesAsync(Guid tenantId);
    Task<IoTDevice> CreateAsync(IoTDevice device);
    Task<IoTDevice> UpdateAsync(IoTDevice device);
    Task<bool> DeleteAsync(Guid id, Guid tenantId);
    Task<bool> ExistsAsync(Guid id, Guid tenantId);
    Task<bool> UpdateStatusAsync(Guid id, DeviceStatus status, Guid tenantId);
    Task<bool> RecordHeartbeatAsync(Guid id, Guid tenantId);
    Task<IEnumerable<DeviceAccessLog>> GetAccessLogsAsync(Guid deviceId, Guid tenantId, DateTime? from = null, DateTime? to = null);
    Task LogAccessAsync(DeviceAccessLog log);
}
