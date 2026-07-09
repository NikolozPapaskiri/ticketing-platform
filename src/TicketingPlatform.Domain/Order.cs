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

    public required string CustomerEmail { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }

    public OrderStatus Status { get; private set; } = OrderStatus.PendingPayment;

    /// <summary>The provider's charge id - the audit link between our order and their money movement.</summary>
    public string? ProviderChargeId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

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
}

public enum OrderStatus
{
    PendingPayment,
    Confirmed,
    PaymentFailed
}
