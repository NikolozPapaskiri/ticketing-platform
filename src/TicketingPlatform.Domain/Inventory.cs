namespace TicketingPlatform.Api.Domain;

/// <summary>
/// Available stock for a ticket type. AvailableQuantity is the contested resource that Phase 5
/// must decrement safely under concurrent purchases. The DbContext maps Postgres's system xmin
/// column as an optimistic-concurrency token for this entity (no explicit property needed).
/// </summary>
public class Inventory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public Guid TicketTypeId { get; set; }
    public TicketType TicketType { get; set; } = null!;

    public int TotalQuantity { get; set; }
    public int AvailableQuantity { get; set; }
}
