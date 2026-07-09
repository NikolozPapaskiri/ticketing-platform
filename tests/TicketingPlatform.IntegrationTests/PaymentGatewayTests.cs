using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Application.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The resilient payment client against a scriptable provider (WireMock): retries on 5xx,
/// no retries on a decline, typed failure instead of an exception when the provider is down.
/// </summary>
[Collection(nameof(ApiCollection))]
public class PaymentGatewayTests
{
    private readonly TicketingApiFactory _factory;
    private readonly IPaymentGateway _gateway;

    public PaymentGatewayTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _gateway = factory.Services.GetRequiredService<IPaymentGateway>();
    }

    private static PaymentCharge Charge() => new(Guid.NewGuid().ToString(), 49.90m, "USD");

    [Fact]
    public async Task Charge_ProviderSucceeds_ReturnsChargeId()
    {
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { chargeId = "ch_ok" }));

        var result = await _gateway.ChargeAsync(Charge(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("ch_ok", result.ProviderChargeId);
    }

    [Fact]
    public async Task Charge_TransientFailureThenSuccess_IsRetriedToSuccess()
    {
        // First attempt 500, second 200: the resilience pipeline must absorb the blip.
        // Retrying is only safe because every charge carries an Idempotency-Key.
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .InScenario("retry").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(500));
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .InScenario("retry").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { chargeId = "ch_after_retry" }));

        var result = await _gateway.ChargeAsync(Charge(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("ch_after_retry", result.ProviderChargeId);
    }

    [Fact]
    public async Task Charge_Declined_IsNotRetried()
    {
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(402));

        var result = await _gateway.ChargeAsync(Charge(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PaymentFailure.Declined, result.Failure);
        // A decline is an ANSWER: retrying it would just re-ask a question already answered.
        Assert.Single(_factory.PaymentProvider.LogEntries);
    }

    [Fact]
    public async Task Charge_ProviderDown_ReturnsUnavailable_NotException()
    {
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var result = await _gateway.ChargeAsync(Charge(), CancellationToken.None);

        // Retries exhausted -> typed failure. A payment outage degrades; it never 500s the buyer.
        Assert.False(result.Succeeded);
        Assert.Equal(PaymentFailure.ProviderUnavailable, result.Failure);
        Assert.True(_factory.PaymentProvider.LogEntries.Count() > 1); // proves retries happened
    }
}
