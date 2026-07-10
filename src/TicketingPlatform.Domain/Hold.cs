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

    /// <summary>Set for self-service customer holds; null for organizer staff box-office holds.</summary>
    public Guid? CustomerUserId { get; set; }

    public int Quantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public HoldStatus Status { get; private set; } = HoldStatus.Active;

    /// <summary>
    /// Set while a checkout owns this hold (PaymentPending). It is NOT a TTL: an expired lease
    /// means "reconciliation is due", never "payment failed" - a lost provider response cannot
    /// prove whether money moved. The reconciler queries the provider before deciding.
    /// </summary>
    public DateTimeOffset? PaymentLeaseUntil { get; private set; }

    /// <summary>When the checkout claim was taken (audit + reconciliation ordering).</summary>
    public DateTimeOffset? PaymentAttemptedAt { get; private set; }

    /// <summary>When a payment outcome was finally resolved (confirmed, declined, or reconciled).</summary>
    public DateTimeOffset? PaymentReconciledAt { get; private set; }

    /// <summary>An Active hold past its TTL. A PaymentPending hold is deliberately NOT expirable here.</summary>
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

    /// <summary>
    /// Take ownership for a payment attempt: Active -> PaymentPending. The DB update that persists
    /// this is guarded by the row's concurrency token, so exactly one checkout can win the claim;
    /// the loser's save conflicts and it returns a 409.
    /// </summary>
    public void ClaimForPayment(DateTimeOffset now, DateTimeOffset leaseUntil)
    {
        if (Status != HoldStatus.Active)
            throw new InvalidOperationException($"Cannot claim a hold in status '{Status}' for payment.");
        if (now >= ExpiresAt)
            throw new InvalidOperationException("Cannot claim an expired hold for payment.");
        Status = HoldStatus.PaymentPending;
        PaymentLeaseUntil = leaseUntil;
        PaymentAttemptedAt = now;
    }

    /// <summary>Provider confirmed the charge: PaymentPending -> Confirmed (permanently sold).</summary>
    public void ConfirmFromPayment(DateTimeOffset now)
    {
        if (Status != HoldStatus.PaymentPending)
            throw new InvalidOperationException($"Cannot confirm a hold in status '{Status}' from payment.");
        Status = HoldStatus.Confirmed;
        PaymentLeaseUntil = null;
        PaymentReconciledAt = now;
    }

    /// <summary>Definitive decline while the TTL still holds: PaymentPending -> Active (buyer may retry).</summary>
    public void ReturnToActiveFromPayment(DateTimeOffset now)
    {
        if (Status != HoldStatus.PaymentPending)
            throw new InvalidOperationException($"Cannot reactivate a hold in status '{Status}'.");
        Status = HoldStatus.Active;
        PaymentLeaseUntil = null;
        PaymentReconciledAt = now;
    }

    /// <summary>Reconciliation proved no charge and the TTL has passed: PaymentPending -> Expired.</summary>
    public void ExpireFromPayment(DateTimeOffset now)
    {
        if (Status != HoldStatus.PaymentPending)
            throw new InvalidOperationException($"Cannot expire a hold in status '{Status}' from payment.");
        Status = HoldStatus.Expired;
        PaymentLeaseUntil = null;
        PaymentReconciledAt = now;
    }

    /// <summary>Ambiguous provider outcome: keep the claim but push the reconciliation deadline out.</summary>
    public void ExtendPaymentLease(DateTimeOffset leaseUntil)
    {
        if (Status != HoldStatus.PaymentPending)
            throw new InvalidOperationException($"Cannot extend the lease of a hold in status '{Status}'.");
        PaymentLeaseUntil = leaseUntil;
    }
}

public enum HoldStatus
{
    Active,          // reserving inventory
    PaymentPending,  // a checkout owns it while payment is in flight (durable, leased)
    Confirmed,       // converted to a purchase
    Released,        // given back explicitly
    Expired          // given back by TTL or by reconciliation proving no charge
}
