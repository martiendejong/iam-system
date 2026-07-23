using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class VisitorService : IVisitorService
{
    private readonly IAMDbContext _context;

    public VisitorService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<Visitor> PreRegisterVisitorAsync(
        Guid tenantId,
        Guid hostUserId,
        string name,
        string email,
        string? company,
        DateTime visitDate,
        string? purpose,
        List<VisitorAccessGrantRequest>? accessGrants,
        CancellationToken ct = default)
    {
        var visitor = new Visitor
        {
            TenantId = tenantId,
            HostUserId = hostUserId,
            Name = name.Trim(),
            Email = email.Trim(),
            Company = company?.Trim(),
            VisitDate = visitDate,
            Purpose = purpose?.Trim(),
            Status = VisitorStatus.PreRegistered
        };

        _context.Visitors.Add(visitor);

        // Create access grants if provided
        if (accessGrants != null && accessGrants.Count > 0)
        {
            foreach (var grant in accessGrants)
            {
                var accessGrant = new VisitorAccessGrant
                {
                    VisitorId = visitor.Id,
                    Resources = JsonSerializer.Serialize(grant.Resources),
                    ValidFrom = grant.ValidFrom,
                    ValidUntil = grant.ValidUntil
                };
                _context.VisitorAccessGrants.Add(accessGrant);
            }
        }

        await _context.SaveChangesAsync(ct);
        return visitor;
    }

    public async Task<List<Visitor>> GetVisitorsAsync(
        Guid tenantId,
        int skip = 0,
        int take = 20,
        VisitorStatus? statusFilter = null,
        CancellationToken ct = default)
    {
        var query = _context.Visitors
            .Include(v => v.HostUser)
            .Include(v => v.AccessGrants)
            .Where(v => v.TenantId == tenantId);

        if (statusFilter.HasValue)
        {
            query = query.Where(v => v.Status == statusFilter.Value);
        }

        return await query
            .OrderByDescending(v => v.VisitDate)
            .ThenByDescending(v => v.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<Visitor?> GetVisitorAsync(
        Guid visitorId,
        CancellationToken ct = default)
    {
        return await _context.Visitors
            .Include(v => v.HostUser)
            .Include(v => v.Tenant)
            .Include(v => v.AccessGrants)
            .FirstOrDefaultAsync(v => v.Id == visitorId, ct);
    }

    public async Task<Visitor?> CheckInAsync(
        Guid visitorId,
        CancellationToken ct = default)
    {
        var visitor = await _context.Visitors
            .FirstOrDefaultAsync(v => v.Id == visitorId, ct);

        if (visitor == null)
            return null;

        if (visitor.Status != VisitorStatus.PreRegistered)
            return visitor; // Already checked in, checked out, or cancelled

        visitor.Status = VisitorStatus.CheckedIn;
        visitor.CheckInAt = DateTime.UtcNow;
        visitor.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return visitor;
    }

    public async Task<Visitor?> CheckOutAsync(
        Guid visitorId,
        CancellationToken ct = default)
    {
        var visitor = await _context.Visitors
            .FirstOrDefaultAsync(v => v.Id == visitorId, ct);

        if (visitor == null)
            return null;

        if (visitor.Status != VisitorStatus.CheckedIn)
            return visitor; // Can only check out if currently checked in

        visitor.Status = VisitorStatus.CheckedOut;
        visitor.CheckOutAt = DateTime.UtcNow;
        visitor.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return visitor;
    }

    public async Task<string?> GetQrTokenAsync(
        Guid visitorId,
        CancellationToken ct = default)
    {
        var visitor = await _context.Visitors
            .FirstOrDefaultAsync(v => v.Id == visitorId, ct);

        return visitor?.QrToken;
    }
}
