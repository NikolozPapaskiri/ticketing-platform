using TicketingPlatform.Domain;

namespace TicketingPlatform.UnitTests.Domain;

/// <summary>
/// Phase A, slice 1. The seat map's whole reason for being versioned is that a published layout can
/// never change underneath tickets already sold against it. These pin that rule at the domain level,
/// where it is enforced by construction rather than by remembering to check.
/// </summary>
public class SeatMapVersionTests
{
    private static SeatMapVersion DraftMap() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        HallId = Guid.NewGuid(),
        Version = 1,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void ADraftMap_CanBeBuiltUp()
    {
        var map = DraftMap();

        var stalls = map.AddSection(Guid.NewGuid(), "Stalls", displayOrder: 1);
        var rowA = stalls.AddRow(Guid.NewGuid(), "A", displayOrder: 1);
        var seat = rowA.AddSeat(Guid.NewGuid(), "12", x: 10.5m, y: 4m);

        Assert.False(map.IsPublished);
        Assert.Single(map.Sections);
        Assert.Single(stalls.Rows);
        Assert.Equal("12", seat.Number);
        Assert.Equal(SeatKind.Sellable, seat.Kind);
        // Identity and geometry are separate concerns living side by side.
        Assert.Equal(10.5m, seat.MapX);
    }

    [Fact]
    public void PublishingFreezesTheLayout()
    {
        var map = DraftMap();
        map.AddSection(Guid.NewGuid(), "Stalls", 1);

        map.Publish(DateTimeOffset.UtcNow);

        Assert.True(map.IsPublished);
        // Re-striping a published hall must not edit tickets already sold against it: the layout
        // change has to become a NEW version instead.
        var error = Assert.Throws<InvalidOperationException>(
            () => map.AddSection(Guid.NewGuid(), "Balcony", 2));
        Assert.Contains("immutable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishingTwice_IsRejected()
    {
        var map = DraftMap();
        map.Publish(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => map.Publish(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RowsOfAPublishedMap_CannotGainSeatsThroughTheirSection()
    {
        // The guard has to hold through the whole aggregate, not just at its root - otherwise you
        // freeze the map and then quietly mutate it one level down.
        var map = DraftMap();
        var stalls = map.AddSection(Guid.NewGuid(), "Stalls", 1);
        stalls.SeatMapVersion = map;   // as EF would wire it when loading the graph

        map.Publish(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => stalls.AddRow(Guid.NewGuid(), "B", 2));
    }
}
