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
    /// The date this ticket type is sold for. REQUIRED as of the contract step - price and capacity
    /// are per-DATE, not per-show: a Saturday night and a Tuesday matinee of the same production are
    /// different money and different availability, so a ticket type that belongs to no date cannot
    /// be either of those things.
    ///
    /// It arrived nullable, was backfilled, had every read moved onto it, and only then became
    /// required. Expand, migrate, contract - no release in that sequence was ever broken.
    /// </summary>
    public Guid PerformanceId { get; set; }
    public Performance Performance { get; set; } = null!;

    /// <summary>
    /// The date a ticket of this type admits you to. Unlike <see cref="Event.HeadlineDate"/>, which
    /// summarises a whole run for a listing, this is a single specific night: printing anything else
    /// on a ticket is the difference between being let in and being turned away at the door.
    ///
    /// Requires Performance to be loaded.
    /// </summary>
    public DateTimeOffset AdmissionDate => Performance.StartsAt;

    public required string Name { get; set; }
    public decimal Price { get; set; }
    public required string Currency { get; set; }

    public Inventory Inventory { get; set; } = null!;
}
