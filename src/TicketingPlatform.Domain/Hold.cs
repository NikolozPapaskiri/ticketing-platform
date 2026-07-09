namespace TicketingPlatform.Domain;

/// <summary>
/// A temporary reservation of ticket inventory. Creating a hold decrements
/// Inventory.AvailableQuantity immediately; the quantity is given back when the hold is
/// released or expires. Confirmed is the Phase 5 path (a paid hold becomes an order).
/// Phase 2 scope is single-threaded correctness only - making this safe under concurrent
/// buyers is the Phase 5 oversell-prevention work.
/// </summary>
public class Hold
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public Guid TicketTypeId { get; set; }
    public TicketType TicketType { get; set; } = null!;

    public int Quantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public HoldStatus Status { get; private set; } = HoldStatus.Active;

    /// <summary>An Active hold past its TTL. Status flips via Expire(), driven by the caller's clock.</summary>
    public bool IsExpired(DateTimeOffset now) => Status == HoldStatus.Active && now >= ExpiresAt;

    public bool CanRelease => Status == HoldStatus.Active;

    public void Release()
    {
        if (!CanRelease)
            throw new InvalidOperationException($"Cannot release a hold in status '{Status}'.");
        Status = HoldStatus.Released;
    }

    public void Expire()
    {
        if (Status != HoldStatus.Active)
            throw new InvalidOperationException($"Cannot expire a hold in status '{Status}'.");
        Status = HoldStatus.Expired;
    }

    /// <summary>A paid hold becomes a sale: the reserved quantity is now permanently sold.</summary>
    public void Confirm()
    {
        if (Status != HoldStatus.Active)
            throw new InvalidOperationException($"Cannot confirm a hold in status '{Status}'.");
        Status = HoldStatus.Confirmed;
    }
}

public enum HoldStatus
{
    Active,     // reserving inventory
    Confirmed,  // converted to a purchase (Phase 5)
    Released,   // given back explicitly
    Expired     // given back by TTL
}
