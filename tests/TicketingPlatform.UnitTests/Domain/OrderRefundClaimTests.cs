using TicketingPlatform.Domain;

namespace TicketingPlatform.UnitTests.Domain;

/// <summary>
/// Gate 0 / G3 + G4: the refund claim carries who asked for it, and reverting the claim leaves no
/// residue. Money leaving is the transition that gets audited, so the entity must not lie about it.
/// </summary>
public class OrderRefundClaimTests
{
    private static Order Confirmed()
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            HoldId = Guid.NewGuid(),
            CustomerEmail = "buyer@test.local",
            Amount = 25m,
            Currency = "USD",
            CreatedAt = DateTimeOffset.UtcNow
        };
        order.MarkConfirmed("ch_test");
        return order;
    }

    [Fact]
    public void ClaimingARefund_RecordsWhoAskedForIt()
    {
        var order = Confirmed();
        var now = DateTimeOffset.UtcNow;

        order.MarkRefundPending(now, "customer:abc");

        Assert.Equal(OrderStatus.RefundPending, order.Status);
        Assert.Equal(now, order.RefundClaimedAt);
        // Recorded at CLAIM time so it survives the reconciler finishing the job later.
        Assert.Equal("customer:abc", order.RefundInitiatedByActor);
    }

    [Fact]
    public void RevertingTheClaim_LeavesNoStaleRefundMetadata()
    {
        var order = Confirmed();
        order.MarkRefundPending(DateTimeOffset.UtcNow, "staff:xyz");

        order.RevertRefundClaim();

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        // A Confirmed order that still carried a claim timestamp would be a lie the next query
        // written against that column inherits.
        Assert.Null(order.RefundClaimedAt);
        Assert.Null(order.RefundInitiatedByActor);
    }

    [Fact]
    public void RevertingWhenNotClaimed_IsRejected()
    {
        var order = Confirmed();

        Assert.Throws<InvalidOperationException>(() => order.RevertRefundClaim());
    }
}
