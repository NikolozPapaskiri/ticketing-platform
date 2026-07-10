using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// PR 1 (payment-state-safety) - the DELIBERATELY RED suite. These tests use barrier-based
/// coordination (not WireMock timing) to reproduce, exactly, the confirmed post-reservation
/// payment defects: a payment racing hold expiry, two checkouts charging one hold, a concurrent
/// idempotency-key insert 500, and a crash between provider success and the final commit.
///
/// They are EXPECTED TO FAIL against the current checkout workflow. Each assertion encodes the
/// invariant the durable payment state machine (PaymentPending + lease + reconciliation) must
/// satisfy; do not "fix" a test by relaxing it. Green is achieved by the implementation commit,
/// not by editing these expectations.
/// </summary>
[Collection(nameof(PaymentRaceCollection))]
public sealed class PaymentRaceTests
{
    private readonly PaymentRaceApiFactory _factory;
    private readonly HttpClient _client;

    public PaymentRaceTests(PaymentRaceApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        ResetHarness();
    }

    private void ResetHarness()
    {
        _factory.Gateway.Reset();
        _factory.Gateway.ChargeGate.Arm(0);         // clear any leftover blockers
        _factory.Fault.Reset();
        _factory.Fault.IdempotencyClaimGate.Arm(0);
    }

    // ---- P0: two checkouts of one hold must not charge or confirm twice -------------------

    [Fact]
    public async Task ConcurrentCheckout_SameHold_ChargesExactlyOnce()
    {
        var ctx = await ArrangeOnSaleHoldAsync(capacity: 5, quantity: 1);

        // Freeze the winner's charge. Today (buggy) BOTH checkouts reach the charge and bill the
        // card; once fixed, the atomic Active->PaymentPending claim lets only ONE reach the
        // charge and the other is rejected before any money moves. Arm(1) captures both worlds:
        // exactly one charge should ever be in flight.
        _factory.Gateway.ChargeGate.Arm(1);
        var first = PostOrderAsync(ctx.CustomerToken, ctx.HoldId, idempotencyKey: "checkout-A");
        var second = PostOrderAsync(ctx.CustomerToken, ctx.HoldId, idempotencyKey: "checkout-B");

        await _factory.Gateway.ChargeGate.WaitForArrivalsAsync(1, TimeSpan.FromSeconds(15));
        _factory.Gateway.ChargeGate.Release();
        var results = await Task.WhenAll(first, second);

        // Money invariant: one logical seat, one provider charge.
        Assert.Equal(1, _factory.Gateway.DistinctChargeCount);
        // Inventory/hold invariant: at most one successful order per hold.
        Assert.Equal(1, await ConfirmedOrdersForHoldAsync(ctx.HoldId));
        // Exactly one HTTP 201; the other checkout must be a 409, never a second sale.
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
    }

    // ---- P0: concurrent same-key requests must replay the winner, not 500 -----------------

    [Fact]
    public async Task ConcurrentCheckout_SameIdempotencyKey_ReplaysWinnerWithout500()
    {
        var ctx = await ArrangeOnSaleHoldAsync(capacity: 5, quantity: 1);

        // Hold BOTH requests at the idempotency-claim insert until each is past the
        // "record not found" read - forcing the unique-index collision deterministically.
        _factory.Fault.IdempotencyClaimGate.Arm(2);
        const string key = "duplicate-key";
        var first = PostOrderAsync(ctx.CustomerToken, ctx.HoldId, idempotencyKey: key);
        var second = PostOrderAsync(ctx.CustomerToken, ctx.HoldId, idempotencyKey: key);

        await _factory.Fault.IdempotencyClaimGate.WaitForArrivalsAsync(2, TimeSpan.FromSeconds(15));
        _factory.Fault.IdempotencyClaimGate.Release();
        var results = await Task.WhenAll(first, second);

        // Neither concurrent request may surface a raw 500 from the unique-index violation.
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, results.Select(r => r.StatusCode));
        // And the key must map to exactly one charge and one order.
        Assert.Equal(1, _factory.Gateway.DistinctChargeCount);
        Assert.Equal(1, await ConfirmedOrdersForHoldAsync(ctx.HoldId));
    }

    // ---- P0: payment in flight must not let expiry resell the seat ------------------------

    [Fact]
    public async Task Checkout_WhenPaymentCrossesHoldExpiry_DoesNotReleaseSoldInventory()
    {
        var ctx = await ArrangeOnSaleHoldAsync(capacity: 1, quantity: 1); // one seat, now held (available 0)

        // Freeze the buyer's charge. Once fixed, the claim has already flipped the hold to
        // PaymentPending by this point, so the seat is being paid for - not idle inventory.
        _factory.Gateway.ChargeGate.Arm(1);
        var buyer = PostOrderAsync(ctx.CustomerToken, ctx.HoldId, idempotencyKey: "buyer-1");
        await _factory.Gateway.ChargeGate.WaitForArrivalsAsync(1, TimeSpan.FromSeconds(15));

        // Give the expiry worker (TTL 3s, scan 1s) time to run while the payment is in flight.
        // INVARIANT: a hold that is being paid for must never be reclaimed. Today the worker
        // expires the still-Active hold and releases the seat (availability -> 1); once fixed the
        // hold is PaymentPending and the worker skips it, so the seat stays sold-out.
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Equal(0, await AvailabilityAsync(ctx.StaffToken, ctx.EventId));

        // Release the payment: it confirms the seat it legitimately still owns.
        _factory.Gateway.ChargeGate.Release();
        var result = await buyer;
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);

        Assert.Equal(1, await ConfirmedOrdersForTicketTypeAsync(ctx.TicketTypeId));
        Assert.Equal(0, await AvailabilityAsync(ctx.StaffToken, ctx.EventId)); // sold, never oversold
    }

    // ---- P0: crash after provider success must recover, not strand InProgress ------------

    [Fact]
    public async Task Checkout_CrashAfterProviderSuccess_RecoversOriginalOrder()
    {
        var ctx = await ArrangeOnSaleHoldAsync(capacity: 5, quantity: 1);

        // Simulate a crash after the provider charged but before the confirmation commit.
        _factory.Fault.FailNextOrderConfirmSave = true;
        const string key = "recoverable-key";

        var crashed = await PostOrderAsync(ctx.CustomerToken, ctx.HoldId, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.InternalServerError, crashed.StatusCode); // the crash fired
        Assert.Equal(1, _factory.Gateway.DistinctChargeCount);                // ...after charging once

        // Retrying the SAME key must recover the original order, not return 409 forever.
        var recovered = await PostOrderAsync(ctx.CustomerToken, ctx.HoldId, idempotencyKey: key);
        Assert.Contains(recovered.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Created });
        Assert.Equal(1, _factory.Gateway.DistinctChargeCount);                // recovery must not re-charge
        Assert.Equal(1, await ConfirmedOrdersForHoldAsync(ctx.HoldId));
    }

    // ---- arrange / assert helpers --------------------------------------------------------

    private sealed record HoldContext(
        string StaffToken, string CustomerToken, Guid EventId, Guid TicketTypeId, Guid HoldId);

    private async Task<HoldContext> ArrangeOnSaleHoldAsync(int capacity, int quantity)
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish");
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 20m, currency = "USD", totalQuantity = capacity });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        var customer = await CreateCustomerAsync();
        var holdId = await CreateCustomerHoldAsync(customer, tt.Id, quantity);
        return new HoldContext(staff, customer.Token, ev.Id, tt.Id, holdId);
    }

    private Task<(string Email, string Password, string Token)> CreateCustomerAsync() =>
        _client.CreateCustomerAsync();

    private async Task<Guid> CreateCustomerHoldAsync(
        (string Email, string Password, string Token) customer, Guid ticketTypeId, int quantity = 1)
    {
        var response = await _client.PostAsAsync(customer.Token, "/api/v1/customer/holds",
            new { ticketTypeId, quantity });
        response.EnsureSuccessStatusCode();
        var hold = (await response.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;
        return hold.Id;
    }

    private Task<HttpResponseMessage> PostOrderAsync(string token, Guid holdId, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/customer/orders")
        {
            Content = JsonContent.Create(new { holdId })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
    }

    private async Task<int> AvailabilityAsync(string staff, Guid eventId)
    {
        var graph = await (await _client.GetAsAsync(staff, $"/api/v1/events/{eventId}"))
            .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        return graph!.TicketTypes.Single().AvailableQuantity;
    }

    private async Task<int> ConfirmedOrdersForHoldAsync(Guid holdId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        return await db.Orders.IgnoreQueryFilters()
            .CountAsync(o => o.HoldId == holdId && o.Status == OrderStatus.Confirmed);
    }

    private async Task<int> ConfirmedOrdersForTicketTypeAsync(Guid ticketTypeId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        return await db.Orders.IgnoreQueryFilters()
            .CountAsync(o => o.Hold.TicketTypeId == ticketTypeId && o.Status == OrderStatus.Confirmed);
    }
}

[CollectionDefinition(nameof(PaymentRaceCollection))]
public sealed class PaymentRaceCollection : ICollectionFixture<PaymentRaceApiFactory>;
