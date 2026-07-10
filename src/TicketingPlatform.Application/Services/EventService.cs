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

    /// <summary>Accepted upload types; the extension doubles as the stored file's suffix.</summary>
    private static readonly Dictionary<string, string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp"
    };
    public const int MaxImageBytes = 2 * 1024 * 1024;

    private readonly IEventRepository _events;
    private readonly ICacheService _cache;
    private readonly IFileStorage _files;

    public EventService(IEventRepository events, ICacheService cache, IFileStorage files)
    {
        _events = events;
        _cache = cache;
        _files = files;
    }

    /// <summary>Requests carry the category as a validated string; null means Other.</summary>
    private static EventCategory ParseCategory(string? category) =>
        Enum.TryParse<EventCategory>(category, ignoreCase: true, out var parsed) ? parsed : EventCategory.Other;

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

    public async Task<Result<PagedResponse<PublicEventListItemResponse>>> ListPublicAsync(
        string tenantSlug, int page, int pageSize, CancellationToken ct)
    {
        var tenant = await _events.GetTenantBySlugAsync(tenantSlug, ct);
        if (tenant is null)
            return Result<PagedResponse<PublicEventListItemResponse>>.NotFound($"Tenant '{tenantSlug}' was not found.");

        var totalCount = await _events.CountPublicOnSaleAsync(tenant.Id, ct);
        var events = await _events.ListPublicOnSaleAsync(tenant.Id, page, pageSize, ct);
        var items = events
            .Select(e => new PublicEventListItemResponse(e.Id, e.Name, e.VenueName, e.StartsAt))
            .ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Result<PagedResponse<PublicEventListItemResponse>>.Success(
            new PagedResponse<PublicEventListItemResponse>(items, page, pageSize, totalCount, totalPages));
    }

    public async Task<Result<PublicEventResponse>> GetPublicByIdAsync(
        string tenantSlug, Guid eventId, CancellationToken ct)
    {
        var tenant = await _events.GetTenantBySlugAsync(tenantSlug, ct);
        if (tenant is null)
            return Result<PublicEventResponse>.NotFound($"Tenant '{tenantSlug}' was not found.");

        var ev = await _events.GetPublicOnSaleWithGraphAsync(tenant.Id, eventId, ct);
        return ev is null
            ? Result<PublicEventResponse>.NotFound($"Event '{eventId}' was not found.")
            : Result<PublicEventResponse>.Success(new PublicEventResponse(
                ev.Id,
                ev.Name,
                ev.Description,
                ev.VenueName,
                ev.StartsAt,
                ev.WaitingRoomEnabled,
                ev.TicketTypes.Select(tt => new TicketTypeResponse(
                    tt.Id,
                    tt.Name,
                    tt.Price,
                    tt.Currency,
                    tt.Inventory.TotalQuantity,
                    tt.Inventory.AvailableQuantity)).ToList()));
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
            Category = ParseCategory(request.Category),
            WaitingRoomEnabled = request.WaitingRoomEnabled ?? false,
            CreatedAt = DateTimeOffset.UtcNow
            // Status defaults to Draft — the entity owns that invariant.
        };

        _events.Add(ev);
        await _events.SaveChangesAsync(ct);

        return new EventResponse(
            ev.Id, ev.Name, ev.Description, ev.VenueName, ev.StartsAt, ev.Status.ToString(),
            ev.Category.ToString(), ev.ImagePath is not null, ev.WaitingRoomEnabled, []);
    }

    public async Task<Result<EventResponse>> UpdateAsync(Guid tenantId, Guid id, UpdateEventRequest request, CancellationToken ct)
    {
        var ev = await _events.GetForUpdateAsync(id, ct);
        if (ev is null)
            return Result<EventResponse>.NotFound($"Event '{id}' was not found.");

        ev.UpdateDetails(request.Name, request.Description, request.VenueName, request.StartsAt,
            ParseCategory(request.Category));
        // Null means "not sent" (older clients), not "turn it off".
        if (request.WaitingRoomEnabled is { } waitingRoom)
            ev.WaitingRoomEnabled = waitingRoom;
        await _events.SaveChangesAsync(ct);

        await _cache.RemoveAsync(EventGraphKey(tenantId, id), ct);
        return Result<EventResponse>.Success(new EventResponse(
            ev.Id, ev.Name, ev.Description, ev.VenueName, ev.StartsAt, ev.Status.ToString(),
            ev.Category.ToString(), ev.ImagePath is not null, ev.WaitingRoomEnabled, []));
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

    // --- Marketplace: the global cross-tenant catalog behind /public/events.
    public async Task<Result<PagedResponse<MarketplaceEventResponse>>> ListMarketplaceAsync(
        MarketplaceFilter filter, int page, int pageSize, CancellationToken ct)
    {
        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(filter.TenantSlug))
        {
            var tenant = await _events.GetTenantBySlugAsync(filter.TenantSlug, ct);
            if (tenant is null)
                return Result<PagedResponse<MarketplaceEventResponse>>.NotFound($"Tenant '{filter.TenantSlug}' was not found.");
            tenantId = tenant.Id;
        }

        EventCategory? category = string.IsNullOrWhiteSpace(filter.Category) ? null : ParseCategory(filter.Category);

        var totalCount = await _events.CountMarketplaceAsync(category, filter.From, filter.To, filter.Query, tenantId, ct);
        var rows = await _events.ListMarketplaceAsync(category, filter.From, filter.To, filter.Query, tenantId, page, pageSize, ct);

        var items = rows.Select(r => new MarketplaceEventResponse(
            r.Id, r.Name, r.VenueName, r.StartsAt, r.Category.ToString(),
            r.TenantName, r.TenantSlug, r.PriceFrom, r.Currency, r.ImagePath is not null)).ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Result<PagedResponse<MarketplaceEventResponse>>.Success(
            new PagedResponse<MarketplaceEventResponse>(items, page, pageSize, totalCount, totalPages));
    }

    public async Task<Result<MarketplaceEventDetailResponse>> GetMarketplaceEventAsync(Guid eventId, CancellationToken ct)
    {
        var detail = await _events.GetMarketplaceEventAsync(eventId, ct);
        if (detail is null)
            return Result<MarketplaceEventDetailResponse>.NotFound($"Event '{eventId}' was not found.");

        var ev = detail.Event;
        return Result<MarketplaceEventDetailResponse>.Success(new MarketplaceEventDetailResponse(
            ev.Id, ev.Name, ev.Description, ev.VenueName, ev.StartsAt, ev.Category.ToString(),
            detail.TenantName, detail.TenantSlug, ev.ImagePath is not null, ev.WaitingRoomEnabled,
            ev.TicketTypes.Select(tt => new TicketTypeResponse(
                tt.Id, tt.Name, tt.Price, tt.Currency,
                tt.Inventory.TotalQuantity, tt.Inventory.AvailableQuantity)).ToList()));
    }

    // --- Event image: stored via the IFileStorage port, streamed anonymously for the catalog.
    public async Task<Result> SetImageAsync(Guid tenantId, Guid eventId, byte[] content, string contentType, CancellationToken ct)
    {
        if (!ImageContentTypes.TryGetValue(contentType, out var extension))
            return Result.Conflict("Unsupported image type. Use JPEG, PNG, or WebP.");
        if (content.Length is 0 or > MaxImageBytes)
            return Result.Conflict($"Image must be between 1 byte and {MaxImageBytes / (1024 * 1024)} MB.");

        var ev = await _events.GetForUpdateAsync(eventId, ct); // tenant-scoped: foreign event => null
        if (ev is null)
            return Result.NotFound($"Event '{eventId}' was not found.");

        var path = $"event-images/{tenantId}/{eventId}{extension}";
        await _files.SaveAsync(path, content, ct);
        ev.SetImage(path);
        await _events.SaveChangesAsync(ct);

        await _cache.RemoveAsync(EventGraphKey(tenantId, eventId), ct);
        return Result.Success();
    }

    public async Task<(Stream Stream, string ContentType)?> GetImageAsync(Guid eventId, CancellationToken ct)
    {
        var path = await _events.GetImagePathAsync(eventId, ct);
        if (path is null)
            return null;

        var stream = await _files.OpenReadAsync(path, ct);
        if (stream is null)
            return null;

        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        return (stream, contentType);
    }

    private static EventResponse Map(Event ev) => new(
        ev.Id,
        ev.Name,
        ev.Description,
        ev.VenueName,
        ev.StartsAt,
        ev.Status.ToString(),
        ev.Category.ToString(),
        ev.ImagePath is not null,
        ev.WaitingRoomEnabled,
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
