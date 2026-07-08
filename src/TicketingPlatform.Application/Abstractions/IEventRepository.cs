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
}
