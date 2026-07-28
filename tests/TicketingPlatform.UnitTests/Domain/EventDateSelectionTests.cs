using TicketingPlatform.Domain;

namespace TicketingPlatform.UnitTests.Domain;

/// <summary>
/// Phase A slice 3, the migrate step. Two different questions get two different answers, and
/// confusing them is the bug this slice exists to make impossible: "when is this event on?" is a
/// summary of a run, while "when does this ticket admit me?" is one specific night.
/// </summary>
public class EventDateSelectionTests
{
    private static Event AnEvent() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Name = "Run of the play",
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
        var ev = AnEvent();
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
        var ev = AnEvent();
        var opening = ANight(ev, now.AddDays(10));
        ANight(ev, now.AddDays(11));

        opening.Cancel(now);

        // Advertising a night you have called off sends customers to a dark theatre.
        Assert.Equal(now.AddDays(11), ev.HeadlineDate);
    }

    [Fact]
    public void AnEventWithNoNightsScheduled_HasNoDate()
    {
        var ev = AnEvent();

        // Null is the honest answer for a show whose dates are not set, and for a run whose every
        // night has been called off. The dropped column could only ever invent one.
        Assert.Null(ev.HeadlineDate);
    }

    [Fact]
    public void AnEventWhoseEveryNightIsCancelled_HasNoDate()
    {
        var now = DateTimeOffset.UtcNow;
        var ev = AnEvent();
        var only = ANight(ev, now.AddDays(10));

        only.Cancel(now);

        Assert.Null(ev.HeadlineDate);
    }

    [Fact]
    public void ATicketsAdmissionDate_IsItsOwnNight_NotTheRunsOpening()
    {
        var now = DateTimeOffset.UtcNow;
        var ev = AnEvent();
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
