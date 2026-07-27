using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence.Scopes;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

public sealed class EventRepository : IEventRepository
{
    private readonly TicketingDbContext _db;
    private readonly PublicScope _public;

    public EventRepository(TicketingDbContext db, PublicScope publicScope)
    {
        _db = db;
        _public = publicScope;
    }

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

    public Task<Tenant?> GetTenantBySlugAsync(string slug, CancellationToken ct) =>
        _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, ct);

    public async Task<IReadOnlyList<PublicEventRow>> ListPublicOnSaleAsync(Guid tenantId, int page, int pageSize, CancellationToken ct) =>
        // Projected in SQL: the public plane never materialises an Event entity.
        await _public.OnSaleEvents()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.StartsAt)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new PublicEventRow(e.Id, e.Name, e.VenueName, e.StartsAt))
            .ToListAsync(ct);

    public Task<int> CountPublicOnSaleAsync(Guid tenantId, CancellationToken ct) =>
        _public.OnSaleEvents()
            .CountAsync(e => e.TenantId == tenantId, ct);

    public Task<PublicEventDetail?> GetPublicOnSaleWithGraphAsync(Guid tenantId, Guid eventId, CancellationToken ct) =>
        _public.OnSaleEvents()
            .AsNoTracking()
            .Where(e => e.Id == eventId && e.TenantId == tenantId)
            .Select(e => new PublicEventDetail(
                e.Id, e.Name, e.Description, e.VenueName, e.StartsAt, e.WaitingRoomEnabled,
                e.TicketTypes.Select(tt => new PublicTicketTypeRow(
                    tt.Id, tt.Name, tt.Price, tt.Currency,
                    tt.Inventory.TotalQuantity, tt.Inventory.AvailableQuantity)).ToList()))
            .FirstOrDefaultAsync(ct);

    // --- Marketplace: the cross-tenant catalog. Composable IQueryable, filters applied only
    // when present (same deferred-execution pattern as the staff browse).
    private IQueryable<Event> MarketplaceQuery(EventCategory? category, DateTimeOffset? from,
        DateTimeOffset? to, string? query, Guid? tenantId)
    {
        // Anonymous scope has no tenant; PublicScope bakes in the OnSale visibility rule.
        var events = _public.OnSaleEvents().AsNoTracking();

        if (category is not null)
            events = events.Where(e => e.Category == category);
        if (from is not null)
            events = events.Where(e => e.StartsAt >= from);
        if (to is not null)
            events = events.Where(e => e.StartsAt <= to);
        if (!string.IsNullOrWhiteSpace(query))
            // ILike = Postgres case-insensitive LIKE; provider-specific SQL belongs HERE,
            // behind the port, which is the whole argument for the repository layer.
            events = events.Where(e =>
                EF.Functions.ILike(e.Name, $"%{query}%") ||
                (e.VenueName != null && EF.Functions.ILike(e.VenueName, $"%{query}%")));
        if (tenantId is not null)
            events = events.Where(e => e.TenantId == tenantId);

        return events;
    }

    public Task<int> CountMarketplaceAsync(EventCategory? category, DateTimeOffset? from, DateTimeOffset? to,
        string? query, Guid? tenantId, CancellationToken ct) =>
        MarketplaceQuery(category, from, to, query, tenantId).CountAsync(ct);

    public async Task<IReadOnlyList<MarketplaceEventRow>> ListMarketplaceAsync(EventCategory? category,
        DateTimeOffset? from, DateTimeOffset? to, string? query, Guid? tenantId, int page, int pageSize,
        CancellationToken ct) =>
        await MarketplaceQuery(category, from, to, query, tenantId)
            .OrderBy(e => e.StartsAt)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            // Tenants carry no query filter (top-level owners), so this join works anonymously.
            // PriceFrom/Currency come from MIN-price subqueries computed in SQL, not in memory.
            .Join(_db.Tenants,
                e => e.TenantId,
                t => t.Id,
                (e, t) => new MarketplaceEventRow(
                    e.Id,
                    e.Name,
                    e.VenueName,
                    e.StartsAt,
                    e.Category,
                    e.ImagePath,
                    t.Name,
                    t.Slug,
                    e.TicketTypes.Min(tt => (decimal?)tt.Price),
                    e.TicketTypes.OrderBy(tt => tt.Price).Select(tt => tt.Currency).FirstOrDefault()))
            .ToListAsync(ct);

    public Task<MarketplaceEventDetail?> GetMarketplaceEventAsync(Guid eventId, CancellationToken ct) =>
        // One projected query instead of entity + follow-up tenant read. Tenants carry no query
        // filter (top-level owners), so the join works anonymously.
        _public.OnSaleEvents()
            .AsNoTracking()
            .Where(e => e.Id == eventId)
            .Join(_db.Tenants, e => e.TenantId, t => t.Id, (e, t) => new MarketplaceEventDetail(
                e.Id, e.Name, e.Description, e.VenueName, e.StartsAt, e.Category,
                t.Name, t.Slug, e.ImagePath != null, e.WaitingRoomEnabled,
                e.TicketTypes.Select(tt => new PublicTicketTypeRow(
                    tt.Id, tt.Name, tt.Price, tt.Currency,
                    tt.Inventory.TotalQuantity, tt.Inventory.AvailableQuantity)).ToList()))
            .FirstOrDefaultAsync(ct);

    public async Task<string?> GetImagePathAsync(Guid eventId, CancellationToken ct) =>
        await _public.EventsIncludingUnpublished()
            .Where(e => e.Id == eventId)
            .Select(e => e.ImagePath)
            .FirstOrDefaultAsync(ct);

    public Task<EventWaitingRoomState?> GetWaitingRoomStateAsync(Guid eventId, CancellationToken ct) =>
        _public.EventsIncludingUnpublished() // anonymous queue endpoints: no tenant on the request
            .AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new EventWaitingRoomState(e.Status == EventStatus.OnSale, e.WaitingRoomEnabled))
            .FirstOrDefaultAsync(ct);
}
