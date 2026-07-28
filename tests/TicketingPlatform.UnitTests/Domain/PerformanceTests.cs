using TicketingPlatform.Domain;

namespace TicketingPlatform.UnitTests.Domain;

/// <summary>
/// Phase A, slice 2. The point of separating the show from the date is that dates behave
/// independently - these pin the parts of that which are domain rules rather than schema.
/// </summary>
public class PerformanceTests
{
    private static Performance Scheduled(DateTimeOffset startsAt) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        EventId = Guid.NewGuid(),
        StartsAt = startsAt,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void AFuturePerformance_IsSellable()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(Scheduled(now.AddDays(7)).IsSellable(now));
    }

    [Fact]
    public void APastPerformance_IsNotSellable()
    {
        var now = DateTimeOffset.UtcNow;

        // The date, not the parent event, decides whether this occurrence can still be bought.
        Assert.False(Scheduled(now.AddMinutes(-1)).IsSellable(now));
    }

    [Fact]
    public void CancellingOneDate_StopsThatDateSelling()
    {
        var now = DateTimeOffset.UtcNow;
        var performance = Scheduled(now.AddDays(7));

        performance.Cancel(now);

        Assert.Equal(PerformanceStatus.Cancelled, performance.Status);
        Assert.Equal(now, performance.CancelledAt);
        Assert.False(performance.IsSellable(now)); // even though the date is still in the future
    }

    [Fact]
    public void CancellingTwice_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var performance = Scheduled(now.AddDays(7));
        performance.Cancel(now);

        // Terminal on purpose: reviving a cancelled date would resurrect tickets already refunded.
        Assert.Throws<InvalidOperationException>(() => performance.Cancel(now));
    }

    [Fact]
    public void CancellingADate_LeavesItsSiblingsAlone()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var friday = Scheduled(now.AddDays(7));
        var saturday = Scheduled(now.AddDays(8));
        friday.EventId = saturday.EventId = eventId;

        friday.Cancel(now);

        // This independence is the whole reason the show and the date are separate entities.
        Assert.False(friday.IsSellable(now));
        Assert.True(saturday.IsSellable(now));
    }
}
