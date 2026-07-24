using IAM.Core.Entities;

namespace IAM.Core.Interfaces;

/// <summary>
/// Service for managing buildings within locations
/// </summary>
public interface IBuildingService
{
    Task<Building?> GetByIdAsync(Guid id, Guid tenantId);
    Task<IEnumerable<Building>> GetAllAsync(Guid tenantId, bool includeInactive = false);
    Task<IEnumerable<Building>> GetByLocationAsync(Guid locationId, Guid tenantId);
    Task<Building> CreateAsync(Building building);
    Task<Building> UpdateAsync(Building building);
    Task<bool> DeleteAsync(Guid id, Guid tenantId);
    Task<bool> ExistsAsync(Guid id, Guid tenantId);
    Task<IEnumerable<Floor>> GetFloorsAsync(Guid buildingId, Guid tenantId);
}
