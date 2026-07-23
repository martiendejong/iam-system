using IAM.Core.Entities;

namespace IAM.Core.Interfaces;

/// <summary>
/// Service for managing floors within buildings
/// </summary>
public interface IFloorService
{
    Task<Floor?> GetByIdAsync(Guid id, Guid tenantId);
    Task<IEnumerable<Floor>> GetAllAsync(Guid tenantId, bool includeInactive = false);
    Task<IEnumerable<Floor>> GetByBuildingAsync(Guid buildingId, Guid tenantId);
    Task<Floor> CreateAsync(Floor floor);
    Task<Floor> UpdateAsync(Floor floor);
    Task<bool> DeleteAsync(Guid id, Guid tenantId);
    Task<bool> ExistsAsync(Guid id, Guid tenantId);
    Task<IEnumerable<Room>> GetRoomsAsync(Guid floorId, Guid tenantId);
}
