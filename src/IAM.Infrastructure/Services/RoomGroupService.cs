using IAM.Core.Entities;
using IAM.Core.Interfaces;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class RoomGroupService : IRoomGroupService
{
    private readonly IAMDbContext _context;

    public RoomGroupService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<RoomGroup?> GetByIdAsync(Guid id, Guid tenantId)
    {
        return await _context.RoomGroups
            .Include(rg => rg.Floor)
            .Include(rg => rg.Building)
            .Include(rg => rg.RoomMemberships)
                .ThenInclude(rm => rm.Room)
            .FirstOrDefaultAsync(rg => rg.Id == id && rg.TenantId == tenantId);
    }

    public async Task<IEnumerable<RoomGroup>> GetAllAsync(Guid tenantId, bool includeInactive = false)
    {
        var query = _context.RoomGroups.Where(rg => rg.TenantId == tenantId);

        if (!includeInactive)
        {
            query = query.Where(rg => rg.IsActive);
        }

        return await query
            .Include(rg => rg.Floor)
            .Include(rg => rg.Building)
            .OrderBy(rg => rg.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<RoomGroup>> GetByFloorAsync(Guid floorId, Guid tenantId)
    {
        return await _context.RoomGroups
            .Where(rg => rg.FloorId == floorId && rg.TenantId == tenantId && rg.IsActive)
            .OrderBy(rg => rg.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<RoomGroup>> GetByBuildingAsync(Guid buildingId, Guid tenantId)
    {
        return await _context.RoomGroups
            .Where(rg => rg.BuildingId == buildingId && rg.TenantId == tenantId && rg.IsActive)
            .OrderBy(rg => rg.Name)
            .ToListAsync();
    }

    public async Task<RoomGroup> CreateAsync(RoomGroup roomGroup)
    {
        roomGroup.CreatedAt = DateTime.UtcNow;
        roomGroup.IsActive = true;

        _context.RoomGroups.Add(roomGroup);
        await _context.SaveChangesAsync();

        return roomGroup;
    }

    public async Task<RoomGroup> UpdateAsync(RoomGroup roomGroup)
    {
        roomGroup.UpdatedAt = DateTime.UtcNow;

        _context.RoomGroups.Update(roomGroup);
        await _context.SaveChangesAsync();

        return roomGroup;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        var roomGroup = await _context.RoomGroups
            .FirstOrDefaultAsync(rg => rg.Id == id && rg.TenantId == tenantId);

        if (roomGroup == null)
            return false;

        // Soft delete
        roomGroup.IsActive = false;
        roomGroup.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ExistsAsync(Guid id, Guid tenantId)
    {
        return await _context.RoomGroups
            .AnyAsync(rg => rg.Id == id && rg.TenantId == tenantId);
    }

    public async Task<bool> AddRoomAsync(Guid roomGroupId, Guid roomId, Guid tenantId, string? notes = null)
    {
        // Verify room group exists and belongs to tenant
        var roomGroup = await _context.RoomGroups
            .FirstOrDefaultAsync(rg => rg.Id == roomGroupId && rg.TenantId == tenantId);

        if (roomGroup == null)
            return false;

        // Verify room exists and belongs to tenant
        var room = await _context.Rooms
            .FirstOrDefaultAsync(r => r.Id == roomId && r.TenantId == tenantId);

        if (room == null)
            return false;

        // Check if membership already exists
        var existingMembership = await _context.RoomGroupMemberships
            .FirstOrDefaultAsync(m => m.RoomGroupId == roomGroupId && m.RoomId == roomId);

        if (existingMembership != null)
        {
            // Reactivate if inactive
            if (!existingMembership.IsActive)
            {
                existingMembership.IsActive = true;
                existingMembership.Notes = notes;
                await _context.SaveChangesAsync();
            }
            return true;
        }

        // Create new membership
        var membership = new RoomGroupMembership
        {
            RoomGroupId = roomGroupId,
            RoomId = roomId,
            Notes = notes,
            AddedAt = DateTime.UtcNow,
            IsActive = true
        };

        _context.RoomGroupMemberships.Add(membership);
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> RemoveRoomAsync(Guid roomGroupId, Guid roomId, Guid tenantId)
    {
        // Verify room group belongs to tenant
        var roomGroup = await _context.RoomGroups
            .FirstOrDefaultAsync(rg => rg.Id == roomGroupId && rg.TenantId == tenantId);

        if (roomGroup == null)
            return false;

        var membership = await _context.RoomGroupMemberships
            .FirstOrDefaultAsync(m => m.RoomGroupId == roomGroupId && m.RoomId == roomId);

        if (membership == null)
            return false;

        // Soft delete membership
        membership.IsActive = false;
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<IEnumerable<Room>> GetRoomsAsync(Guid roomGroupId, Guid tenantId)
    {
        // Verify room group belongs to tenant
        var roomGroup = await _context.RoomGroups
            .FirstOrDefaultAsync(rg => rg.Id == roomGroupId && rg.TenantId == tenantId);

        if (roomGroup == null)
            return Enumerable.Empty<Room>();

        return await _context.RoomGroupMemberships
            .Where(m => m.RoomGroupId == roomGroupId && m.IsActive)
            .Include(m => m.Room)
                .ThenInclude(r => r.Floor)
            .Select(m => m.Room)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }
}
