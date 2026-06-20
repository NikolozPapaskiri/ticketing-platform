namespace TicketingPlatform.Domain;

/// <summary>
/// A category of ticket for an event, for example General Admission or VIP. Each ticket type
/// has exactly one Inventory row. Price is a plain decimal here; it becomes a Money value object
/// in the Phase 2 refactor.
/// </summary>
public class TicketType
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public Guid EventId { get; set; }
    public Event Event { get; set; } = null!;

    public required string Name { get; set; }
    public decimal Price { get; set; }
    public required string Currency { get; set; }

    public Inventory Inventory { get; set; } = null!;
}
