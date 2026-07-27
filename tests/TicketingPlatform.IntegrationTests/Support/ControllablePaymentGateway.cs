using System.Collections.Concurrent;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.IntegrationTests.Support;

/// <summary>
/// Test-only <see cref="IPaymentGateway"/> that replaces the real HTTP client. It gives the race
/// tests three things WireMock cannot: (1) an exact charge barrier so a payment can be frozen
/// mid-flight while the rest of the system moves, (2) a count of DISTINCT provider idempotency
/// keys - the true money-invariant metric (a faithful provider dedupes retries of one key but
/// still charges two DIFFERENT keys), and (3) a scriptable response per scenario.
/// </summary>
public sealed class ControllablePaymentGateway : IPaymentGateway
{
    // key -> provider charge id, for every key that produced a SUCCESSFUL charge. Doubles as the
    // provider's memory so GetChargeStatusAsync (reconciliation) can answer truthfully.
    private readonly ConcurrentDictionary<string, string> _charges = new();
    private readonly ConcurrentDictionary<string, byte> _refundKeys = new();

    /// <summary>Freeze a charge mid-flight to force an exact interleaving. Disarmed by default.</summary>
    public AsyncGate ChargeGate { get; } = new();

    /// <summary>Freeze a refund mid-flight (the money-movement analogue of ChargeGate).</summary>
    public AsyncGate RefundGate { get; } = new();

    public Func<PaymentCharge, PaymentResult> ChargeResponder { get; set; } = DefaultCharge;

    private static PaymentResult DefaultCharge(PaymentCharge c) => PaymentResult.Success($"ch_{Guid.NewGuid():N}");

    /// <summary>Distinct provider idempotency keys successfully charged. This is what "charged twice" means.</summary>
    public int DistinctChargeCount => _charges.Count;

    /// <summary>Distinct provider idempotency keys refunded (used by PR 2's refund tests).</summary>
    public int DistinctRefundCount => _refundKeys.Count;

    public void Reset()
    {
        _charges.Clear();
        _refundKeys.Clear();
        _refundStatusQueries = 0;
        RefundStatusResponder = null;
        ChargeResponder = DefaultCharge;
        ChargeGate.Arm(0);
        RefundGate.Arm(0);
    }

    public async Task<PaymentResult> ChargeAsync(PaymentCharge charge, CancellationToken ct)
    {
        await ChargeGate.PassAsync(ct);          // deterministic interleaving seam
        var result = ChargeResponder(charge);
        if (result.Succeeded)
            _charges.TryAdd(charge.IdempotencyKey, result.ProviderChargeId!); // idempotent per key
        return result;
    }

    public async Task<PaymentResult> RefundAsync(PaymentRefund refund, CancellationToken ct)
    {
        await RefundGate.PassAsync(ct);          // deterministic interleaving seam
        _refundKeys.TryAdd(refund.IdempotencyKey, 0);
        return PaymentResult.Success($"rf_{Guid.NewGuid():N}");
    }

    public Task<PaymentInquiry> GetChargeStatusAsync(string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(_charges.TryGetValue(idempotencyKey, out var chargeId)
            ? PaymentInquiry.Charged(chargeId)
            : PaymentInquiry.NotCharged());

    /// <summary>
    /// The refund-side mirror. <see cref="_refundKeys"/> is the provider's memory of which refund
    /// keys it has seen, so a stranded refund can be settled from provider truth instead of being
    /// re-issued. Override to script Pending/Unknown recovery scenarios.
    /// </summary>
    public Func<string, RefundInquiry>? RefundStatusResponder { get; set; }

    /// <summary>How many times recovery asked the provider about a refund - proves G1's path runs.</summary>
    public int RefundStatusQueryCount => _refundStatusQueries;
    private int _refundStatusQueries;

    public Task<RefundInquiry> GetRefundStatusAsync(string refundIdempotencyKey, CancellationToken ct)
    {
        Interlocked.Increment(ref _refundStatusQueries);
        return Task.FromResult(RefundStatusResponder?.Invoke(refundIdempotencyKey)
            ?? (_refundKeys.ContainsKey(refundIdempotencyKey)
                ? RefundInquiry.Refunded($"rf_{refundIdempotencyKey}")
                : RefundInquiry.NotRefunded()));
    }
}
