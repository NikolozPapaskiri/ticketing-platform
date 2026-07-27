using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for event persistence. Use-case-shaped methods; all reads are tenant-scoped by the
/// EF global query filter in the implementation, so a foreign tenant's event is simply invisible.
/// </summary>
public interface IEventRepository
{
    Task<int> CountAsync(EventStatus? status, CancellationToken ct);

    /// <summary>One page, ordered by StartsAt with Id as tiebreaker (stable offset paging).</summary>
    Task<IReadOnlyList<Event>> ListPageAsync(EventStatus? status, int page, int pageSize, CancellationToken ct);

    /// <summary>Full graph (ticket types + inventory), read-only.</summary>
    Task<Event?> GetWithGraphAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked load for a state change; SaveChangesAsync persists the mutation.</summary>
    Task<Event?> GetForUpdateAsync(Guid id, CancellationToken ct);

    Task<bool> ExistsAsync(Guid id, CancellationToken ct);

    void Add(Event ev);
    void AddTicketType(TicketType ticketType);
    Task SaveChangesAsync(CancellationToken ct);

    Task<Tenant?> GetTenantBySlugAsync(string slug, CancellationToken ct);
    Task<IReadOnlyList<PublicEventRow>> ListPublicOnSaleAsync(Guid tenantId, int page, int pageSize, CancellationToken ct);
    Task<int> CountPublicOnSaleAsync(Guid tenantId, CancellationToken ct);
    Task<PublicEventDetail?> GetPublicOnSaleWithGraphAsync(Guid tenantId, Guid eventId, CancellationToken ct);

    // --- Marketplace: the CROSS-TENANT public catalog. Implementations read through PublicScope
    // (an anonymous request has no tenant, so the filter would return nothing), which bakes in the
    // OnSale visibility rule. TenantId narrows to one organizer when the storefront filters by slug.
    // Everything here returns PROJECTED rows: a public read must never hand back an entity that
    // could carry private fields.
    Task<int> CountMarketplaceAsync(EventCategory? category, DateTimeOffset? from, DateTimeOffset? to,
        string? query, Guid? tenantId, CancellationToken ct);
    Task<IReadOnlyList<MarketplaceEventRow>> ListMarketplaceAsync(EventCategory? category, DateTimeOffset? from,
        DateTimeOffset? to, string? query, Guid? tenantId, int page, int pageSize, CancellationToken ct);

    /// <summary>Cross-tenant OnSale event graph + its tenant, or null (hidden/unknown => 404 upstream).</summary>
    Task<MarketplaceEventDetail?> GetMarketplaceEventAsync(Guid eventId, CancellationToken ct);

    /// <summary>Image path regardless of status (organizers preview drafts); null when no image.</summary>
    Task<string?> GetImagePathAsync(Guid eventId, CancellationToken ct);

    /// <summary>
    /// Cross-tenant, two-column lookup for the waiting-room endpoints (they are polled by every
    /// queued browser, so no graph load). Null when the event does not exist.
    /// </summary>
    Task<EventWaitingRoomState?> GetWaitingRoomStateAsync(Guid eventId, CancellationToken ct);
}

public sealed record EventWaitingRoomState(bool OnSale, bool WaitingRoomEnabled);

/// <summary>SQL-projected catalog row: PriceFrom is MIN(ticket price) computed in the database.</summary>
public sealed record MarketplaceEventRow(
    Guid Id,
    string Name,
    string? VenueName,
    DateTimeOffset StartsAt,
    EventCategory Category,
    string? ImagePath,
    string TenantName,
    string TenantSlug,
    decimal? PriceFrom,
    string? Currency);

/// <summary>Projected rows for the public planes - deliberately NOT entities (Track 3 / T1).</summary>
public sealed record PublicEventRow(Guid Id, string Name, string? VenueName, DateTimeOffset StartsAt);

public sealed record PublicTicketTypeRow(
    Guid Id,
    string Name,
    decimal Price,
    string Currency,
    int TotalQuantity,
    int AvailableQuantity);

public sealed record PublicEventDetail(
    Guid Id,
    string Name,
    string? Description,
    string? VenueName,
    DateTimeOffset StartsAt,
    bool WaitingRoomEnabled,
    IReadOnlyList<PublicTicketTypeRow> TicketTypes);

public sealed record MarketplaceEventDetail(
    Guid Id,
    string Name,
    string? Description,
    string? VenueName,
    DateTimeOffset StartsAt,
    EventCategory Category,
    string TenantName,
    string TenantSlug,
    bool HasImage,
    bool WaitingRoomEnabled,
    IReadOnlyList<PublicTicketTypeRow> TicketTypes);
