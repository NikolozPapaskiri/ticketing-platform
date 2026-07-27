namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for the external payment provider. The IdempotencyKey is the contract's most important
/// field: the provider must treat two charges with the same key as ONE charge, which is what
/// makes retrying a timed-out request safe. Without it, "retry on timeout" can double-charge -
/// the classic non-idempotent-retry trap.
/// </summary>
public interface IPaymentGateway
{
    Task<PaymentResult> ChargeAsync(PaymentCharge charge, CancellationToken ct);
    Task<PaymentResult> RefundAsync(PaymentRefund refund, CancellationToken ct);

    /// <summary>
    /// Reconciliation lookup: given the stable idempotency key of a charge, ask the provider what
    /// actually happened. This is how we recover after a crash or lost response WITHOUT charging
    /// again - the provider, not our database, is the authority on whether money moved.
    /// </summary>
    Task<PaymentInquiry> GetChargeStatusAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// The same reconciliation lookup for money going the OTHER way. Without it, recovering a
    /// stranded refund means re-calling <see cref="RefundAsync"/> with the stable key and trusting
    /// the provider to have kept its idempotency record for the whole recovery horizon - an
    /// assumption about someone else's retention policy rather than a guarantee. It also cannot
    /// tell "refund completed, response lost" from "refund still processing" from "no refund
    /// exists". Asking is how both money directions settle against provider truth.
    /// </summary>
    Task<RefundInquiry> GetRefundStatusAsync(string refundIdempotencyKey, CancellationToken ct);
}

public sealed record PaymentCharge(string IdempotencyKey, decimal Amount, string Currency);
public sealed record PaymentRefund(string IdempotencyKey, string ProviderChargeId, decimal Amount, string Currency);

public enum PaymentFailure
{
    None,
    Declined,            // the provider said no (4xx) - retrying will not help
    ProviderUnavailable  // network / 5xx / circuit open - retrying LATER may help
}

public sealed record PaymentResult(bool Succeeded, string? ProviderChargeId, PaymentFailure Failure)
{
    public static PaymentResult Success(string providerChargeId) => new(true, providerChargeId, PaymentFailure.None);
    public static PaymentResult Declined() => new(false, null, PaymentFailure.Declined);
    public static PaymentResult Unavailable() => new(false, null, PaymentFailure.ProviderUnavailable);
}

/// <summary>Definitive answer that a charge either happened, did not, or is not yet knowable.</summary>
public enum PaymentOutcome
{
    Charged,      // the provider confirms a successful charge for this key
    NotCharged,   // the provider confirms no charge exists for this key
    Pending,      // the provider knows the key but the result is not final yet
    Unknown       // the provider could not be reached / gave no usable answer (retry later)
}

public sealed record PaymentInquiry(PaymentOutcome Outcome, string? ProviderChargeId)
{
    public static PaymentInquiry Charged(string providerChargeId) => new(PaymentOutcome.Charged, providerChargeId);
    public static PaymentInquiry NotCharged() => new(PaymentOutcome.NotCharged, null);
    public static PaymentInquiry Pending() => new(PaymentOutcome.Pending, null);
    public static PaymentInquiry Unknown() => new(PaymentOutcome.Unknown, null);
}

/// <summary>The refund-side mirror of <see cref="PaymentOutcome"/>.</summary>
public enum RefundOutcome
{
    Refunded,     // the provider confirms a completed refund for this key
    NotRefunded,  // the provider confirms no refund exists for this key
    Pending,      // the provider knows the key but the refund is not final yet
    Unknown       // unreachable / no usable answer - fall back to a keyed retry
}

public sealed record RefundInquiry(RefundOutcome Outcome, string? ProviderRefundId)
{
    public static RefundInquiry Refunded(string providerRefundId) => new(RefundOutcome.Refunded, providerRefundId);
    public static RefundInquiry NotRefunded() => new(RefundOutcome.NotRefunded, null);
    public static RefundInquiry Pending() => new(RefundOutcome.Pending, null);
    public static RefundInquiry Unknown() => new(RefundOutcome.Unknown, null);
}
