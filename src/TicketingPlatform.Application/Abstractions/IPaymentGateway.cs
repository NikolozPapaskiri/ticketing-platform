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
