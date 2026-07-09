using TicketingPlatform.Domain;

namespace TicketingPlatform.UnitTests.Domain;

public class InventoryReservationTests
{
    private static Inventory Stock(int total, int available) =>
        new() { TotalQuantity = total, AvailableQuantity = available };

    [Theory]
    [InlineData(100, 100, 10, true, 90)]   // normal reserve
    [InlineData(100, 10, 10, true, 0)]     // exact remaining stock is reservable
    [InlineData(100, 5, 10, false, 5)]     // more than available: rejected, stock untouched
    [InlineData(100, 0, 1, false, 0)]      // sold out
    [InlineData(100, 50, 0, false, 50)]    // zero is not a reservation
    [InlineData(100, 50, -3, false, 50)]   // negative can never mint stock
    public void TryReserve_EnforcesAvailability(int total, int available, int quantity, bool expected, int expectedAvailable)
    {
        var inventory = Stock(total, available);

        var reserved = inventory.TryReserve(quantity);

        Assert.Equal(expected, reserved);
        Assert.Equal(expectedAvailable, inventory.AvailableQuantity);
    }

    [Fact]
    public void Release_RestoresQuantity()
    {
        var inventory = Stock(100, 90);

        inventory.Release(10);

        Assert.Equal(100, inventory.AvailableQuantity);
    }

    [Fact]
    public void Release_ClampsAtCapacity()
    {
        // A double-release must never push availability above what physically exists.
        var inventory = Stock(100, 95);

        inventory.Release(10);

        Assert.Equal(100, inventory.AvailableQuantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Release_NonPositive_Throws(int quantity)
    {
        var inventory = Stock(100, 50);

        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.Release(quantity));
    }
}
