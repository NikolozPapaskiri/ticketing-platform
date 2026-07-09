using TicketingPlatform.Domain;

namespace TicketingPlatform.UnitTests.Domain;

public class HoldTests
{
    private static readonly DateTimeOffset Expiry = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Hold ActiveHold() => new()
    {
        Id = Guid.NewGuid(),
        Quantity = 2,
        CreatedAt = Expiry.AddMinutes(-10),
        ExpiresAt = Expiry
    };

    [Fact]
    public void NewHold_IsActive() => Assert.Equal(HoldStatus.Active, ActiveHold().Status);

    [Theory]
    [InlineData(-1, false)]  // one second before expiry: still live
    [InlineData(0, true)]    // exactly at expiry: expired (>= boundary)
    [InlineData(1, true)]    // past expiry
    public void IsExpired_ComparesAgainstExpiry(int secondsOffset, bool expected)
    {
        var hold = ActiveHold();

        Assert.Equal(expected, hold.IsExpired(Expiry.AddSeconds(secondsOffset)));
    }

    [Fact]
    public void Release_SetsReleased_AndBlocksSecondRelease()
    {
        var hold = ActiveHold();

        hold.Release();

        Assert.Equal(HoldStatus.Released, hold.Status);
        Assert.False(hold.CanRelease);
        Assert.Throws<InvalidOperationException>(hold.Release);
    }

    [Fact]
    public void Expire_OnReleasedHold_Throws()
    {
        var hold = ActiveHold();
        hold.Release();

        Assert.Throws<InvalidOperationException>(hold.Expire);
    }

    [Fact]
    public void ReleasedHold_IsNotExpired()
    {
        // Expired means "Active past TTL" - a released hold already gave its quantity back.
        var hold = ActiveHold();
        hold.Release();

        Assert.False(hold.IsExpired(Expiry.AddHours(1)));
    }
}
