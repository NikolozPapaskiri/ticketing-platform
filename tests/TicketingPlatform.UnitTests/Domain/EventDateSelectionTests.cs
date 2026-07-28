using TicketingPlatform.Domain;

namespace TicketingPlatform.UnitTests.Domain;

/// <summary>
/// Phase A slice 3, the migrate step. Two different questions get two different answers, and
/// confusing them is the bug this slice exists to make impossible: "when is this event on?" is a
/// summary of a run, while "when does this ticket admit me?" is one specific night.
/// </summary>
public class EventDateSelectionTests
{
    private static Event AnEvent(DateTimeOffset legacyStartsAt) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Name = "Run of the play",
        StartsAt = legacyStartsAt,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static Performance ANight(Event ev, DateTimeOffset startsAt)
    {
        var performance = new Performance
        {
            Id = Guid.NewGuid(),
            TenantId = ev.TenantId,
            EventId = ev.Id,
            StartsAt = startsAt,
            CreatedAt = DateTimeOffset.UtcNow
        };
        ev.Performances.Add(performance);
        return performance;
    }

    [Fact]
    public void TheHeadlineDate_IsTheEarliestNight()
    {
        var now = DateTimeOffset.UtcNow;
        var ev = AnEvent(now.AddYears(5)); // legacy column deliberately wrong
        ANight(ev, now.AddDays(20));
        ANight(ev, now.AddDays(10));
        ANight(ev, now.AddDays(30));

        // A thirty-night run is listed under the night it opens.
        Assert.Equal(now.AddDays(10), ev.HeadlineDate);
    }

    [Fact]
    public void ACancelledNight_IsNeverTheHeadlineDate()
    {
        var now = DateTimeOffset.UtcNow;
        var ev = AnEvent(now.AddYears(5));
        var opening = ANight(ev, now.AddDays(10));
        ANight(ev, now.AddDays(11));

        opening.Cancel(now);

        // Advertising a night you have called off sends customers to a dark theatre.
        Assert.Equal(now.AddDays(11), ev.HeadlineDate);
    }

    [Fact]
    public void AnEventWithNoNightsYet_FallsBackToTheLegacyColumn()
    {
        var startsAt = DateTimeOffset.UtcNow.AddDays(3);
        var ev = AnEvent(startsAt);

        // What makes the migrate step releasable: rows the backfill has not reached still answer.
        Assert.Equal(startsAt, ev.HeadlineDate);
    }

    [Fact]
    public void ATicketsAdmissionDate_IsItsOwnNight_NotTheRunsOpening()
    {
        var now = DateTimeOffset.UtcNow;
        var ev = AnEvent(now.AddDays(10));
        ANight(ev, now.AddDays(10));
        var closingNight = ANight(ev, now.AddDays(30));

        var ticketType = new TicketType
        {
            Id = Guid.NewGuid(),
            TenantId = ev.TenantId,
            EventId = ev.Id,
            Event = ev,
            PerformanceId = closingNight.Id,
            Performance = closingNight,
            Name = "Stalls",
            Currency = "USD"
        };

        // The listing says the run opens on the 10th; this ticket is for the 30th. Printing the
        // opening night on it is how a customer gets turned away at the door.
        Assert.Equal(now.AddDays(30), ticketType.AdmissionDate);
        Assert.Equal(now.AddDays(10), ev.HeadlineDate);
    }

}
