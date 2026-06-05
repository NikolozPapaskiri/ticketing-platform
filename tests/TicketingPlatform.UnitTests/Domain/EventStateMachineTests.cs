using TicketingPlatform.Api.Domain;

namespace TicketingPlatform.UnitTests;

public class EventStateMachineTests
{
    [Theory]
    [InlineData(EventStatus.Draft, EventStatus.OnSale, true)]
    [InlineData(EventStatus.OnSale, EventStatus.OnSale, false)]
    [InlineData(EventStatus.Draft, EventStatus.Draft, false)]
    [InlineData(EventStatus.Draft, EventStatus.Closed, true)]
    [InlineData(EventStatus.OnSale, EventStatus.Draft, false)]
    [InlineData(EventStatus.OnSale, EventStatus.Closed, true)]
    [InlineData(EventStatus.Closed, EventStatus.Draft, false)]
    [InlineData(EventStatus.Closed, EventStatus.OnSale, false)]
    [InlineData(EventStatus.Closed, EventStatus.Closed, false)]
    public void CanTransitionTo_ReturnsWhetherMoveIsAllowed(EventStatus from, EventStatus to, bool expected)
    {
        var ev = EventInStatus(from);
        Assert.Equal(expected, ev.CanTransitionTo(to));
    }

    [Theory]
    [InlineData(EventStatus.Draft, EventStatus.OnSale)]
    [InlineData(EventStatus.Draft, EventStatus.Closed)]
    [InlineData(EventStatus.OnSale, EventStatus.Closed)]
    public void TransitionTo_LegalMove_SetsStatus(EventStatus from, EventStatus to)
    {
        var ev = EventInStatus(from);

        ev.TransitionTo(to);                 // void: it mutates the entity, it does not return

        Assert.Equal(to, ev.Status);         // assert the move actually happened
    }

    [Theory]
    [InlineData(EventStatus.Draft, EventStatus.Draft)]
    [InlineData(EventStatus.OnSale, EventStatus.Draft)]
    [InlineData(EventStatus.OnSale, EventStatus.OnSale)]
    [InlineData(EventStatus.Closed, EventStatus.Draft)]
    [InlineData(EventStatus.Closed, EventStatus.OnSale)]
    [InlineData(EventStatus.Closed, EventStatus.Closed)]
    public void TransitionTo_IllegalMove_Throws(EventStatus from, EventStatus to)
    {
        var ev = EventInStatus(from);

        // Assert.Throws verifies BOTH that it threw and that it threw this specific type.
        Assert.Throws<InvalidStatusTransitionException>(() => ev.TransitionTo(to));
    }

    // Drives a fresh (Draft) event into the requested status through the public API.
    // Both OnSale and Closed are reachable from Draft in one legal hop.
    private static Event EventInStatus(EventStatus status)
    {
        var ev = new Event { Name = "Test" };          // new events start in Draft
        if (status == EventStatus.OnSale) ev.TransitionTo(EventStatus.OnSale);
        else if (status == EventStatus.Closed) ev.TransitionTo(EventStatus.Closed);
        return ev;
    }
}
