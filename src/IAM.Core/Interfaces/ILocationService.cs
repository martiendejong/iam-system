using IAM.Core.Entities;

namespace IAM.Core.Interfaces;

/// <summary>
/// Service for managing locations (campuses, sites, multi-building complexes)
/// </summary>
public interface ILocationService
{
    Task<Location?> GetByIdAsync(Guid id, Guid tenantId);
    Task<IEnumerable<Location>> GetAllAsync(Guid tenantId, bool includeInactive = false);
    Task<Location> CreateAsync(Location location);
    Task<Location> UpdateAsync(Location location);
    Task<bool> DeleteAsync(Guid id, Guid tenantId);
    Task<bool> ExistsAsync(Guid id, Guid tenantId);
    Task<IEnumerable<Building>> GetBuildingsAsync(Guid locationId, Guid tenantId);
}
