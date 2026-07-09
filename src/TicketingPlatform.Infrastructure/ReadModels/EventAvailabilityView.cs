namespace TicketingPlatform.Infrastructure.ReadModels;

/// <summary>
/// The CQRS read model: one denormalized row per ticket type, maintained by the
/// AvailabilityProjectionConsumer from AvailabilityChanged events. The QUERY side of the
/// system reads this table; the WRITE side (holds/orders fighting over Inventories) never
/// sees browse traffic. Eventually consistent - the price of separating the two sides - and
/// self-healing: every projection pass re-reads the live truth rather than applying deltas,
/// so a lost event only means staleness until the next one.
/// </summary>
public class EventAvailabilityView
{
    /// <summary>One row per ticket type; the ticket type id IS the key.</summary>
    public Guid TicketTypeId { get; set; }

    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public required string EventName { get; set; }
    public required string TicketTypeName { get; set; }

    public int Available { get; set; }
    public int Total { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
