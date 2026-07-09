using TicketingPlatform.Application.Contracts;

namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Query port for the CQRS availability read model - the denormalized table the projection
/// consumer maintains. Reads here never touch the transactional Inventories row, so browse
/// traffic during an on-sale cannot contend with the buyers' writes. Eventually consistent by
/// design: the row updates when the AvailabilityChanged event flows through the broker.
/// </summary>
public interface IAvailabilityReadModel
{
    Task<IReadOnlyList<TicketAvailabilityResponse>> GetForEventAsync(Guid eventId, CancellationToken ct);
}
