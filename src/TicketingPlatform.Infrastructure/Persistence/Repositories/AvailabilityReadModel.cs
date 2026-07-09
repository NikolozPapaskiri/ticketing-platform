using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Contracts;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

public sealed class AvailabilityReadModel : IAvailabilityReadModel
{
    private readonly TicketingDbContext _db;
    public AvailabilityReadModel(TicketingDbContext db) => _db = db;

    public async Task<IReadOnlyList<TicketAvailabilityResponse>> GetForEventAsync(Guid eventId, CancellationToken ct) =>
        // Request scope: the tenant filter applies normally - staff see their own events only.
        // This query never touches Inventories: browse load stays off the contested write path.
        await _db.EventAvailability
            .AsNoTracking()
            .Where(v => v.EventId == eventId)
            .OrderBy(v => v.TicketTypeName)
            .Select(v => new TicketAvailabilityResponse(v.TicketTypeId, v.TicketTypeName, v.Available, v.Total, v.UpdatedAt))
            .ToListAsync(ct);
}
