namespace TicketingPlatform.Domain;

/// <summary>
/// ONE scheduled occurrence of an event. The show is not the date.
///
/// A theatre run is one <see cref="Event"/> and thirty performances: same content, same cast page,
/// same images, but different dates, different prices, and independent availability. Platforms that
/// conflate the two force organizers to clone the whole event per date, which produces content drift
/// ("which copy has the corrected running time?") and makes cross-date reporting impossible.
///
/// This is added ADDITIVELY: <see cref="Event.StartsAt"/> still exists and still drives the current
/// GA path. The follow-on slice backfills one performance per existing event and moves
/// TicketType/Inventory onto it - expand, migrate, then contract, so no release is ever broken.
/// </summary>
public class Performance
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public Guid EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// Where this occurrence happens. Nullable while the model is additive, and per-performance
    /// rather than per-event on purpose: a touring production plays a different hall every night.
    /// </summary>
    public Guid? HallId { get; set; }
    public Hall? Hall { get; set; }

    /// <summary>
    /// WHICH layout version this performance sells against. Pinning the version - not just the hall -
    /// is what lets the hall be re-striped later without rewriting seats on tickets already sold for
    /// this date. Null for general admission, which has no seat map.
    /// </summary>
    public Guid? SeatMapVersionId { get; set; }
    public SeatMapVersion? SeatMapVersion { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? DoorsOpenAt { get; set; }

    public PerformanceStatus Status { get; private set; } = PerformanceStatus.Scheduled;
    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Cancelling a single date must not touch its siblings - that is precisely what the split buys.
    /// Terminal by design: "uncancelling" would silently revive tickets that were already refunded,
    /// so the correct recovery is scheduling a new performance.
    /// </summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status == PerformanceStatus.Cancelled)
            throw new InvalidOperationException("This performance is already cancelled.");
        Status = PerformanceStatus.Cancelled;
        CancelledAt = now;
    }

    /// <summary>A cancelled date sells nothing, whatever the parent event's status says.</summary>
    public bool IsSellable(DateTimeOffset now) =>
        Status == PerformanceStatus.Scheduled && StartsAt > now;
}

public enum PerformanceStatus
{
    Scheduled,
    Cancelled
}
