using IAM.Core.Entities;
using IAM.Core.Interfaces;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class IoTDeviceService : IIoTDeviceService
{
    private readonly IAMDbContext _context;

    public IoTDeviceService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<IoTDevice?> GetByIdAsync(Guid id, Guid tenantId)
    {
        return await _context.IoTDevices
            .Include(d => d.Room)
                .ThenInclude(r => r.Floor)
                    .ThenInclude(f => f.Building)
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId);
    }

    public async Task<IoTDevice?> GetByDeviceIdAsync(string deviceId, Guid tenantId)
    {
        return await _context.IoTDevices
            .Include(d => d.Room)
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.TenantId == tenantId);
    }

    public async Task<IEnumerable<IoTDevice>> GetAllAsync(Guid tenantId, bool includeInactive = false)
    {
        var query = _context.IoTDevices.Where(d => d.TenantId == tenantId);

        if (!includeInactive)
        {
            query = query.Where(d => d.IsActive);
        }

        return await query
            .Include(d => d.Room)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<IoTDevice>> GetByRoomAsync(Guid roomId, Guid tenantId)
    {
        return await _context.IoTDevices
            .Where(d => d.RoomId == roomId && d.TenantId == tenantId && d.IsActive)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<IoTDevice>> GetByTypeAsync(DeviceType type, Guid tenantId)
    {
        return await _context.IoTDevices
            .Where(d => d.Type == type && d.TenantId == tenantId && d.IsActive)
            .Include(d => d.Room)
                .ThenInclude(r => r.Floor)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<IoTDevice>> GetByStatusAsync(DeviceStatus status, Guid tenantId)
    {
        return await _context.IoTDevices
            .Where(d => d.Status == status && d.TenantId == tenantId && d.IsActive)
            .Include(d => d.Room)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<IoTDevice>> GetStreamingDevicesAsync(Guid tenantId)
    {
        return await _context.IoTDevices
            .Where(d => d.SupportsStreaming && d.TenantId == tenantId && d.IsActive)
            .Include(d => d.Room)
                .ThenInclude(r => r.Floor)
                    .ThenInclude(f => f.Building)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<IoTDevice> CreateAsync(IoTDevice device)
    {
        device.CreatedAt = DateTime.UtcNow;
        device.IsActive = true;
        device.Status = DeviceStatus.Unknown;

        _context.IoTDevices.Add(device);
        await _context.SaveChangesAsync();

        return device;
    }

    public async Task<IoTDevice> UpdateAsync(IoTDevice device)
    {
        device.UpdatedAt = DateTime.UtcNow;

        _context.IoTDevices.Update(device);
        await _context.SaveChangesAsync();

        return device;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        var device = await _context.IoTDevices
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId);

        if (device == null)
            return false;

        // Soft delete
        device.IsActive = false;
        device.Status = DeviceStatus.Offline;
        device.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ExistsAsync(Guid id, Guid tenantId)
    {
        return await _context.IoTDevices
            .AnyAsync(d => d.Id == id && d.TenantId == tenantId);
    }

    public async Task<bool> UpdateStatusAsync(Guid id, DeviceStatus status, Guid tenantId)
    {
        var device = await _context.IoTDevices
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId);

        if (device == null)
            return false;

        device.Status = status;
        device.UpdatedAt = DateTime.UtcNow;

        if (status == DeviceStatus.Online)
        {
            device.LastSeenAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RecordHeartbeatAsync(Guid id, Guid tenantId)
    {
        var device = await _context.IoTDevices
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId);

        if (device == null)
            return false;

        device.LastSeenAt = DateTime.UtcNow;
        device.Status = DeviceStatus.Online;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<DeviceAccessLog>> GetAccessLogsAsync(Guid deviceId, Guid tenantId, DateTime? from = null, DateTime? to = null)
    {
        // Verify device belongs to tenant
        var device = await _context.IoTDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.TenantId == tenantId);

        if (device == null)
            return Enumerable.Empty<DeviceAccessLog>();

        var query = _context.DeviceAccessLogs
            .Where(l => l.DeviceId == deviceId);

        if (from.HasValue)
        {
            query = query.Where(l => l.AccessedAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(l => l.AccessedAt <= to.Value);
        }

        return await query
            .Include(l => l.User)
            .OrderByDescending(l => l.AccessedAt)
            .ToListAsync();
    }

    public async Task LogAccessAsync(DeviceAccessLog log)
    {
        log.AccessedAt = DateTime.UtcNow;

        _context.DeviceAccessLogs.Add(log);
        await _context.SaveChangesAsync();

        // Update device's LastDataAt if this was a successful stream/control action
        if (log.Success && (log.Action == DeviceAccessAction.Stream || log.Action == DeviceAccessAction.Control))
        {
            var device = await _context.IoTDevices.FindAsync(log.DeviceId);
            if (device != null)
            {
                device.LastDataAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
        }
    }
}
