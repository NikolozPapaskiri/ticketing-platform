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

    /// <summary>
    /// The date this ticket type is sold for. Nullable during the migration, which is what makes
    /// the change releasable: the column is added and backfilled first, every read still goes
    /// through EventId, and only once nothing depends on the old shape does this become required
    /// and EventId go away. Expand, migrate, contract.
    ///
    /// It has to move here eventually because price and capacity are per-DATE, not per-show: a
    /// Saturday night and a Tuesday matinee of the same production are different money and
    /// different availability.
    /// </summary>
    public Guid? PerformanceId { get; set; }
    public Performance? Performance { get; set; }

    public required string Name { get; set; }
    public decimal Price { get; set; }
    public required string Currency { get; set; }

    public Inventory Inventory { get; set; } = null!;
}
