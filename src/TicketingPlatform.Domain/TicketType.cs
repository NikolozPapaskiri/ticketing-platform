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

    /// <summary>
    /// The date a ticket of this type admits you to. Unlike <see cref="Event.HeadlineDate"/>, which
    /// summarises a whole run for a listing, this is a single specific night: printing anything else
    /// on a ticket is the difference between being let in and being turned away at the door.
    ///
    /// Falls back to the legacy column for types with no date yet; the contract step removes that.
    /// Requires Performance and Event to be loaded.
    /// </summary>
    public DateTimeOffset AdmissionDate => Performance?.StartsAt ?? Event.StartsAt;

    public required string Name { get; set; }
    public decimal Price { get; set; }
    public required string Currency { get; set; }

    public Inventory Inventory { get; set; } = null!;
}
