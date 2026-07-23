using IAM.Core.Entities;
using IAM.Core.Interfaces;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class RoomService : IRoomService
{
    private readonly IAMDbContext _context;

    public RoomService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<Room?> GetByIdAsync(Guid id, Guid tenantId)
    {
        return await _context.Rooms
            .Include(r => r.Floor)
                .ThenInclude(f => f.Building)
            .Include(r => r.Devices)
            .Include(r => r.GroupMemberships)
                .ThenInclude(gm => gm.RoomGroup)
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId && r.IsActive);
    }

    public async Task<IEnumerable<Room>> GetAllAsync(Guid tenantId, bool includeInactive = false)
    {
        var query = _context.Rooms.Where(r => r.TenantId == tenantId);

        if (!includeInactive)
        {
            query = query.Where(r => r.IsActive);
        }

        return await query
            .Include(r => r.Floor)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<Room>> GetByFloorAsync(Guid floorId, Guid tenantId)
    {
        return await _context.Rooms
            .Where(r => r.FloorId == floorId && r.TenantId == tenantId && r.IsActive)
            .OrderBy(r => r.RoomNumber)
            .ThenBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<Room>> GetByTypeAsync(RoomType type, Guid tenantId)
    {
        return await _context.Rooms
            .Where(r => r.Type == type && r.TenantId == tenantId && r.IsActive)
            .Include(r => r.Floor)
                .ThenInclude(f => f.Building)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<Room> CreateAsync(Room room)
    {
        room.CreatedAt = DateTime.UtcNow;
        room.IsActive = true;

        _context.Rooms.Add(room);
        await _context.SaveChangesAsync();

        return room;
    }

    public async Task<Room> UpdateAsync(Room room)
    {
        room.UpdatedAt = DateTime.UtcNow;

        _context.Rooms.Update(room);
        await _context.SaveChangesAsync();

        return room;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        var room = await _context.Rooms
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId);

        if (room == null)
            return false;

        // Soft delete
        room.IsActive = false;
        room.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ExistsAsync(Guid id, Guid tenantId)
    {
        return await _context.Rooms
            .AnyAsync(r => r.Id == id && r.TenantId == tenantId);
    }

    public async Task<IEnumerable<IoTDevice>> GetDevicesAsync(Guid roomId, Guid tenantId)
    {
        return await _context.IoTDevices
            .Where(d => d.RoomId == roomId && d.TenantId == tenantId && d.IsActive)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<RoomGroup>> GetGroupsAsync(Guid roomId, Guid tenantId)
    {
        return await _context.RoomGroupMemberships
            .Where(gm => gm.RoomId == roomId && gm.IsActive)
            .Include(gm => gm.RoomGroup)
            .Where(gm => gm.RoomGroup.TenantId == tenantId)
            .Select(gm => gm.RoomGroup)
            .OrderBy(rg => rg.Name)
            .ToListAsync();
    }
}
