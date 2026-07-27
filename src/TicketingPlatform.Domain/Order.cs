namespace TicketingPlatform.Domain;

/// <summary>
/// A purchase built on top of a hold - the saga's aggregate. Lifecycle:
/// PendingPayment -> Confirmed (payment succeeded; the hold converts to a sale) or
/// PendingPayment -> PaymentFailed (the hold stays Active so the buyer can retry until TTL;
/// hold expiry is the saga's compensation - inventory flows back automatically).
/// </summary>
public class Order
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public Guid HoldId { get; set; }
    public Hold Hold { get; set; } = null!;

    /// <summary>
    /// Set for self-service customer checkout. Null for organizer staff box-office sales.
    /// This is the ownership column behind customer resource-based authorization.
    /// </summary>
    public Guid? CustomerUserId { get; set; }

    public required string CustomerEmail { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }

    public OrderStatus Status { get; private set; } = OrderStatus.PendingPayment;

    /// <summary>The provider's charge id - the audit link between our order and their money movement.</summary>
    public string? ProviderChargeId { get; private set; }

    /// <summary>The provider's refund id - null until a confirmed order is refunded.</summary>
    public string? ProviderRefundId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RefundedAt { get; private set; }

    /// <summary>When a refund was claimed (RefundPending). Lets the reconciler find stuck refunds.</summary>
    public DateTimeOffset? RefundClaimedAt { get; private set; }

    /// <summary>
    /// Who asked for the refund - the customer, the staff member, or a background process. Recorded
    /// when the claim is made, so it survives the reconciler finishing the job on their behalf.
    /// </summary>
    public string? RefundInitiatedByActor { get; private set; }

    public void MarkConfirmed(string providerChargeId)
    {
        if (Status != OrderStatus.PendingPayment)
            throw new InvalidOperationException($"Cannot confirm an order in status '{Status}'.");
        Status = OrderStatus.Confirmed;
        ProviderChargeId = providerChargeId;
    }

    public void MarkPaymentFailed()
    {
        if (Status != OrderStatus.PendingPayment)
            throw new InvalidOperationException($"Cannot fail an order in status '{Status}'.");
        Status = OrderStatus.PaymentFailed;
    }

    /// <summary>
    /// Claim the refund before calling the provider: Confirmed -> RefundPending. Guarded by the
    /// row's concurrency token so only one caller (customer or staff) owns the money movement;
    /// the loser resolves to this same order instead of issuing a second provider refund.
    /// </summary>
    public void MarkRefundPending(DateTimeOffset now, string initiatedByActor)
    {
        if (Status != OrderStatus.Confirmed)
            throw new InvalidOperationException($"Cannot start a refund for an order in status '{Status}'.");
        Status = OrderStatus.RefundPending;
        RefundClaimedAt = now;
        // Recorded at CLAIM time, not at completion: the reconciler may be the one that finishes
        // the job, and "who asked for this money to leave" is the first question in any dispute.
        RefundInitiatedByActor = initiatedByActor;
    }

    public void MarkRefunded(string providerRefundId, DateTimeOffset refundedAt)
    {
        if (Status is not (OrderStatus.Confirmed or OrderStatus.RefundPending))
            throw new InvalidOperationException($"Cannot refund an order in status '{Status}'.");
        Status = OrderStatus.Refunded;
        ProviderRefundId = providerRefundId;
        RefundedAt = refundedAt;
    }

    /// <summary>
    /// An ambiguous refund settled as "no refund happened": RefundPending -> Confirmed. Clears the
    /// claim metadata too - leaving RefundClaimedAt set on a Confirmed order makes the row carry a
    /// lie, and the next query written against that column would inherit it. The stale-refund scan
    /// also filters on status today, so this is hygiene rather than a live bug.
    /// </summary>
    public void RevertRefundClaim()
    {
        if (Status != OrderStatus.RefundPending)
            throw new InvalidOperationException($"Cannot revert a refund claim from status '{Status}'.");
        Status = OrderStatus.Confirmed;
        RefundClaimedAt = null;
        RefundInitiatedByActor = null;
    }
}

public enum OrderStatus
{
    PendingPayment,
    Confirmed,
    RefundPending, // a refund is in flight (claimed before the provider call)
    PaymentFailed,
    Refunded
}
