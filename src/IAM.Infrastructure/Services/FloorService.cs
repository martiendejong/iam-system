using IAM.Core.Entities;
using IAM.Core.Interfaces;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class FloorService : IFloorService
{
    private readonly IAMDbContext _context;

    public FloorService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<Floor?> GetByIdAsync(Guid id, Guid tenantId)
    {
        return await _context.Floors
            .Include(f => f.Building)
            .Include(f => f.Rooms)
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId);
    }

    public async Task<IEnumerable<Floor>> GetAllAsync(Guid tenantId, bool includeInactive = false)
    {
        var query = _context.Floors.Where(f => f.TenantId == tenantId);

        if (!includeInactive)
        {
            query = query.Where(f => f.IsActive);
        }

        return await query
            .Include(f => f.Building)
            .OrderBy(f => f.Building.Name)
            .ThenBy(f => f.FloorNumber)
            .ToListAsync();
    }

    public async Task<IEnumerable<Floor>> GetByBuildingAsync(Guid buildingId, Guid tenantId)
    {
        return await _context.Floors
            .Where(f => f.BuildingId == buildingId && f.TenantId == tenantId && f.IsActive)
            .OrderBy(f => f.FloorNumber)
            .ToListAsync();
    }

    public async Task<Floor> CreateAsync(Floor floor)
    {
        floor.CreatedAt = DateTime.UtcNow;
        floor.IsActive = true;

        _context.Floors.Add(floor);
        await _context.SaveChangesAsync();

        return floor;
    }

    public async Task<Floor> UpdateAsync(Floor floor)
    {
        floor.UpdatedAt = DateTime.UtcNow;

        _context.Floors.Update(floor);
        await _context.SaveChangesAsync();

        return floor;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        var floor = await _context.Floors
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId);

        if (floor == null)
            return false;

        // Soft delete
        floor.IsActive = false;
        floor.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ExistsAsync(Guid id, Guid tenantId)
    {
        return await _context.Floors
            .AnyAsync(f => f.Id == id && f.TenantId == tenantId);
    }

    public async Task<IEnumerable<Room>> GetRoomsAsync(Guid floorId, Guid tenantId)
    {
        return await _context.Rooms
            .Where(r => r.FloorId == floorId && r.TenantId == tenantId && r.IsActive)
            .OrderBy(r => r.RoomNumber)
            .ThenBy(r => r.Name)
            .ToListAsync();
    }
}
