using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Services;

/// <summary>
/// Event use cases. Owns querying (via the IEventRepository port), the state-machine
/// orchestration, and DTO mapping. Takes the tenant id as an explicit parameter so the use
/// cases are testable without ambient request context. HTTP concerns (status codes, headers,
/// page-parameter validation) stay in the controller.
/// </summary>
public sealed class EventService
{
    /// <summary>
    /// TTL for the event graph. Every write that changes what this graph shows (transitions,
    /// new ticket types, hold create/release) invalidates the key, so the TTL is only the
    /// backstop for missed invalidations, not the consistency mechanism.
    /// </summary>
    private static readonly TimeSpan EventGraphTtl = TimeSpan.FromSeconds(30);

    private readonly IEventRepository _events;
    private readonly ICacheService _cache;

    public EventService(IEventRepository events, ICacheService cache)
    {
        _events = events;
        _cache = cache;
    }

    private static string EventGraphKey(Guid tenantId, Guid eventId) => CacheKeys.EventGraph(tenantId, eventId);

    public async Task<PagedResponse<EventListItemResponse>> ListAsync(
        int page, int pageSize, EventStatus? status, CancellationToken ct)
    {
        // Count of the *filtered* set first, then the page. Two queries is the standard shape.
        var totalCount = await _events.CountAsync(status, ct);
        var events = await _events.ListPageAsync(status, page, pageSize, ct);

        // Status.ToString() happens here, in memory — it is not reliably translatable in SQL.
        var items = events
            .Select(e => new EventListItemResponse(e.Id, e.Name, e.VenueName, e.StartsAt, e.Status.ToString()))
            .ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return new PagedResponse<EventListItemResponse>(items, page, pageSize, totalCount, totalPages);
    }

    public async Task<Result<EventResponse>> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        // Cache-aside: check cache -> on miss load from the DB and populate with a TTL.
        var key = EventGraphKey(tenantId, id);
        var cached = await _cache.GetAsync<EventResponse>(key, ct);
        if (cached is not null)
            return Result<EventResponse>.Success(cached);

        var ev = await _events.GetWithGraphAsync(id, ct);
        if (ev is null)
            return Result<EventResponse>.NotFound($"Event '{id}' was not found."); // misses are NOT cached

        var response = Map(ev);
        await _cache.SetAsync(key, response, EventGraphTtl, ct);
        return Result<EventResponse>.Success(response);
    }

    public async Task<EventResponse> CreateAsync(Guid tenantId, CreateEventRequest request, CancellationToken ct)
    {
        var ev = new Event
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = request.Name,
            Description = request.Description,
            VenueName = request.VenueName,
            StartsAt = request.StartsAt,
            CreatedAt = DateTimeOffset.UtcNow
            // Status defaults to Draft — the entity owns that invariant.
        };

        _events.Add(ev);
        await _events.SaveChangesAsync(ct);

        return new EventResponse(
            ev.Id, ev.Name, ev.Description, ev.VenueName, ev.StartsAt, ev.Status.ToString(), []);
    }

    public async Task<Result<TicketTypeResponse>> AddTicketTypeAsync(
        Guid tenantId, Guid eventId, CreateTicketTypeRequest request, CancellationToken ct)
    {
        // Tenant-scoped exists check: another tenant's event is invisible => NotFound, not Forbidden.
        if (!await _events.ExistsAsync(eventId, ct))
            return Result<TicketTypeResponse>.NotFound($"Event '{eventId}' was not found.");

        var ticketType = new TicketType
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EventId = eventId,
            Name = request.Name,
            Price = request.Price,
            Currency = request.Currency,
            Inventory = new Inventory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                TotalQuantity = request.TotalQuantity,
                AvailableQuantity = request.TotalQuantity
            }
        };

        _events.AddTicketType(ticketType);
        await _events.SaveChangesAsync(ct);

        // A new ticket type changes the event graph shape - invalidate, don't wait for TTL.
        await _cache.RemoveAsync(EventGraphKey(tenantId, eventId), ct);

        return Result<TicketTypeResponse>.Success(new TicketTypeResponse(
            ticketType.Id,
            ticketType.Name,
            ticketType.Price,
            ticketType.Currency,
            ticketType.Inventory.TotalQuantity,
            ticketType.Inventory.AvailableQuantity));
    }

    public async Task<Result> TransitionAsync(Guid tenantId, Guid id, EventStatus target, CancellationToken ct)
    {
        var ev = await _events.GetForUpdateAsync(id, ct);
        if (ev is null)
            return Result.NotFound($"Event '{id}' was not found.");

        // Pre-check for the expected case; TransitionTo's throw stays as the integrity backstop.
        if (!ev.CanTransitionTo(target))
            return Result.Conflict($"An event in '{ev.Status}' cannot move to '{target}'.");

        ev.TransitionTo(target);
        await _events.SaveChangesAsync(ct);

        // Invalidate AFTER the commit: invalidating before could let a concurrent reader
        // re-cache the old row in the gap.
        await _cache.RemoveAsync(EventGraphKey(tenantId, id), ct);

        return Result.Success();
    }

    private static EventResponse Map(Event ev) => new(
        ev.Id,
        ev.Name,
        ev.Description,
        ev.VenueName,
        ev.StartsAt,
        ev.Status.ToString(),
        ev.TicketTypes
            .Select(tt => new TicketTypeResponse(
                tt.Id,
                tt.Name,
                tt.Price,
                tt.Currency,
                tt.Inventory.TotalQuantity,
                tt.Inventory.AvailableQuantity))
            .ToList());
}
