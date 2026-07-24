using IAM.Core.Entities;
using IAM.Core.Interfaces;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class BuildingService : IBuildingService
{
    private readonly IAMDbContext _context;

    public BuildingService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<Building?> GetByIdAsync(Guid id, Guid tenantId)
    {
        return await _context.Buildings
            .Include(b => b.Location)
            .Include(b => b.Floors)
            .FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId && b.IsActive);
    }

    public async Task<IEnumerable<Building>> GetAllAsync(Guid tenantId, bool includeInactive = false)
    {
        var query = _context.Buildings.Where(b => b.TenantId == tenantId);

        if (!includeInactive)
        {
            query = query.Where(b => b.IsActive);
        }

        return await query
            .Include(b => b.Location)
            .Include(b => b.Floors)
            .OrderBy(b => b.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<Building>> GetByLocationAsync(Guid locationId, Guid tenantId)
    {
        return await _context.Buildings
            .Where(b => b.LocationId == locationId && b.TenantId == tenantId && b.IsActive)
            .Include(b => b.Floors)
            .OrderBy(b => b.Name)
            .ToListAsync();
    }

    public async Task<Building> CreateAsync(Building building)
    {
        building.CreatedAt = DateTime.UtcNow;
        building.IsActive = true;

        _context.Buildings.Add(building);
        await _context.SaveChangesAsync();

        return building;
    }

    public async Task<Building> UpdateAsync(Building building)
    {
        building.UpdatedAt = DateTime.UtcNow;

        _context.Buildings.Update(building);
        await _context.SaveChangesAsync();

        return building;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        var building = await _context.Buildings
            .FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId);

        if (building == null)
            return false;

        // Soft delete
        building.IsActive = false;
        building.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ExistsAsync(Guid id, Guid tenantId)
    {
        return await _context.Buildings
            .AnyAsync(b => b.Id == id && b.TenantId == tenantId);
    }

    public async Task<IEnumerable<Floor>> GetFloorsAsync(Guid buildingId, Guid tenantId)
    {
        return await _context.Floors
            .Where(f => f.BuildingId == buildingId && f.TenantId == tenantId && f.IsActive)
            .OrderBy(f => f.FloorNumber)
            .ToListAsync();
    }
}
