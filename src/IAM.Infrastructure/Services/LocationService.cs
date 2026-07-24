using IAM.Core.Entities;
using IAM.Core.Interfaces;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class LocationService : ILocationService
{
    private readonly IAMDbContext _context;

    public LocationService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<Location?> GetByIdAsync(Guid id, Guid tenantId)
    {
        return await _context.Locations
            .Include(l => l.Buildings)
            .FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId && l.IsActive);
    }

    public async Task<IEnumerable<Location>> GetAllAsync(Guid tenantId, bool includeInactive = false)
    {
        var query = _context.Locations.Where(l => l.TenantId == tenantId);

        if (!includeInactive)
        {
            query = query.Where(l => l.IsActive);
        }

        return await query
            .Include(l => l.Buildings)
            .OrderBy(l => l.Name)
            .ToListAsync();
    }

    public async Task<Location> CreateAsync(Location location)
    {
        location.CreatedAt = DateTime.UtcNow;
        location.IsActive = true;

        _context.Locations.Add(location);
        await _context.SaveChangesAsync();

        return location;
    }

    public async Task<Location> UpdateAsync(Location location)
    {
        location.UpdatedAt = DateTime.UtcNow;

        _context.Locations.Update(location);
        await _context.SaveChangesAsync();

        return location;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        var location = await _context.Locations
            .FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId);

        if (location == null)
            return false;

        // Soft delete
        location.IsActive = false;
        location.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ExistsAsync(Guid id, Guid tenantId)
    {
        return await _context.Locations
            .AnyAsync(l => l.Id == id && l.TenantId == tenantId);
    }

    public async Task<IEnumerable<Building>> GetBuildingsAsync(Guid locationId, Guid tenantId)
    {
        return await _context.Buildings
            .Where(b => b.LocationId == locationId && b.TenantId == tenantId && b.IsActive)
            .OrderBy(b => b.Name)
            .ToListAsync();
    }
}
