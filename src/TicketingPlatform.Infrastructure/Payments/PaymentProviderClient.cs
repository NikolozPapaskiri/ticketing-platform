using System.Net;
using System.Net.Http.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Infrastructure.Payments;

/// <summary>
/// Typed HttpClient for the payment provider. Registered via IHttpClientFactory (handler
/// pooling + DNS rotation - never `new HttpClient()` per call, never one static forever) with
/// Microsoft.Extensions.Http.Resilience's standard pipeline: retry with exponential backoff +
/// jitter, circuit breaker, and per-attempt timeout.
/// Retry safety rules encoded here:
///  - 5xx/timeouts ARE retried (the resilience handler does it) - safe because of the
///    Idempotency-Key header: the provider deduplicates, so a retry cannot double-charge.
///  - 4xx (declined) is NOT retried - a decline is an answer, not a fault.
///  - When the circuit is open or retries are exhausted, the caller gets a typed
///    ProviderUnavailable result, never an exception: a payment outage must degrade, not 500.
/// </summary>
public sealed class PaymentProviderClient : IPaymentGateway
{
    private readonly HttpClient _http;
    public PaymentProviderClient(HttpClient http) => _http = http;

    public async Task<PaymentResult> ChargeAsync(PaymentCharge charge, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "charges");
            request.Headers.Add("Idempotency-Key", charge.IdempotencyKey);
            request.Content = JsonContent.Create(new { amount = charge.Amount, currency = charge.Currency });

            using var response = await _http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<ChargeResponse>(cancellationToken: ct);
                return PaymentResult.Success(body?.ChargeId ?? "unknown");
            }

            // 4xx from a payment provider means "declined" - definitive, do not mask as an outage.
            if (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
                return PaymentResult.Declined();

            return PaymentResult.Unavailable(); // 5xx that survived the retry pipeline
        }
        catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException
                                       or TimeoutRejectedException or TaskCanceledException)
        {
            // Network fault, open circuit, or timeout after retries: typed failure, not a crash.
            return PaymentResult.Unavailable();
        }
    }

    private sealed record ChargeResponse(string ChargeId);
}
