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
