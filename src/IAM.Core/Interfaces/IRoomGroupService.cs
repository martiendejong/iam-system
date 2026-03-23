using IAM.Core.Entities;

namespace IAM.Core.Interfaces;

/// <summary>
/// Service for managing room groups (logical groupings of rooms)
/// </summary>
public interface IRoomGroupService
{
    Task<RoomGroup?> GetByIdAsync(Guid id, Guid tenantId);
    Task<IEnumerable<RoomGroup>> GetAllAsync(Guid tenantId, bool includeInactive = false);
    Task<IEnumerable<RoomGroup>> GetByFloorAsync(Guid floorId, Guid tenantId);
    Task<IEnumerable<RoomGroup>> GetByBuildingAsync(Guid buildingId, Guid tenantId);
    Task<RoomGroup> CreateAsync(RoomGroup roomGroup);
    Task<RoomGroup> UpdateAsync(RoomGroup roomGroup);
    Task<bool> DeleteAsync(Guid id, Guid tenantId);
    Task<bool> ExistsAsync(Guid id, Guid tenantId);
    Task<bool> AddRoomAsync(Guid roomGroupId, Guid roomId, Guid tenantId, string? notes = null);
    Task<bool> RemoveRoomAsync(Guid roomGroupId, Guid roomId, Guid tenantId);
    Task<IEnumerable<Room>> GetRoomsAsync(Guid roomGroupId, Guid tenantId);
}
