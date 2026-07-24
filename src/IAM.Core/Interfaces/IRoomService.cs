using IAM.Core.Entities;

namespace IAM.Core.Interfaces;

/// <summary>
/// Service for managing rooms within floors
/// </summary>
public interface IRoomService
{
    Task<Room?> GetByIdAsync(Guid id, Guid tenantId);
    Task<IEnumerable<Room>> GetAllAsync(Guid tenantId, bool includeInactive = false);
    Task<IEnumerable<Room>> GetByFloorAsync(Guid floorId, Guid tenantId);
    Task<IEnumerable<Room>> GetByTypeAsync(RoomType type, Guid tenantId);
    Task<Room> CreateAsync(Room room);
    Task<Room> UpdateAsync(Room room);
    Task<bool> DeleteAsync(Guid id, Guid tenantId);
    Task<bool> ExistsAsync(Guid id, Guid tenantId);
    Task<IEnumerable<IoTDevice>> GetDevicesAsync(Guid roomId, Guid tenantId);
    Task<IEnumerable<RoomGroup>> GetGroupsAsync(Guid roomId, Guid tenantId);
}
