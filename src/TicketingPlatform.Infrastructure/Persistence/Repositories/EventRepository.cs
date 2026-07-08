using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

public sealed class EventRepository : IEventRepository
{
    private readonly TicketingDbContext _db;
    public EventRepository(TicketingDbContext db) => _db = db;

    // Composable IQueryable: the optional status Where only joins the expression tree when a
    // filter is present; the tenant query filter is always baked in. Nothing executes until
    // Count/ToList. (e.Status == enum translates to SQL via HasConversion<string>().)
    private IQueryable<Event> Filtered(EventStatus? status)
    {
        var query = _db.Events.AsNoTracking();
        if (status is not null)
            query = query.Where(e => e.Status == status);
        return query;
    }

    public Task<int> CountAsync(EventStatus? status, CancellationToken ct) =>
        Filtered(status).CountAsync(ct);

    public async Task<IReadOnlyList<Event>> ListPageAsync(EventStatus? status, int page, int pageSize, CancellationToken ct) =>
        // Stable order needs the Id tiebreaker: offset paging over a non-unique key (StartsAt)
        // is non-deterministic — two events at the same instant could swap between pages.
        await Filtered(status)
            .OrderBy(e => e.StartsAt)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

    public Task<Event?> GetWithGraphAsync(Guid id, CancellationToken ct) =>
        _db.Events
            .AsNoTracking()
            .Include(e => e.TicketTypes)
                .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<Event?> GetForUpdateAsync(Guid id, CancellationToken ct) =>
        // Tracked on purpose: the caller mutates Status and SaveChangesAsync diffs the snapshot.
        _db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        _db.Events.AnyAsync(e => e.Id == id, ct);

    public void Add(Event ev) => _db.Events.Add(ev);

    public void AddTicketType(TicketType ticketType) => _db.TicketTypes.Add(ticketType);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
