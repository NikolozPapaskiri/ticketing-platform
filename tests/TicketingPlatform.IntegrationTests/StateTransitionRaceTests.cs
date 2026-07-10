using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// PR 2 (atomic-state-transitions) - the DELIBERATELY RED suite for the post-payment transitions
/// that still use read-check-write: refund, ticket scan, and hold release. Barrier-coordinated so
/// each race is exact. Expected to FAIL until the atomic transitions land; the assertions encode
/// the invariants (one money movement, one admission, one inventory credit) and must not be relaxed.
/// </summary>
[Collection(nameof(PaymentRaceCollection))]
public sealed class StateTransitionRaceTests
{
    private readonly PaymentRaceApiFactory _factory;
    private readonly HttpClient _client;

    public StateTransitionRaceTests(PaymentRaceApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.Gateway.Reset();
        _factory.Fault.Reset();
    }

    // ---- Refund is one money movement regardless of caller or retry --------------------------

    [Fact]
    public async Task ConcurrentRefund_CustomerAndStaff_ProviderRefundsExactlyOnce()
    {
        var (staff, customer, orderId) = await ArrangeConfirmedCustomerOrderAsync();

        // Freeze the winning refund; both callers reach the provider today because the refund key
        // includes the actor (customer vs staff), so the provider sees two DIFFERENT keys.
        _factory.Gateway.RefundGate.Arm(1);
        var byCustomer = _client.PostAsAsync(customer, $"/api/v1/customer/orders/{orderId}/refund");
        var byStaff = _client.PostAsAsync(staff, $"/api/v1/orders/{orderId}/refund");

        await _factory.Gateway.RefundGate.WaitForArrivalsAsync(1, TimeSpan.FromSeconds(15));
        _factory.Gateway.RefundGate.Release();
        await Task.WhenAll(byCustomer, byStaff);

        Assert.Equal(1, _factory.Gateway.DistinctRefundCount); // one order, one provider refund
        Assert.Equal(1, await OrdersInStatusAsync(orderId, OrderStatus.Refunded));
    }

    // ---- A ticket admits exactly once under concurrent scanners ------------------------------

    [Fact]
    public async Task ConcurrentScan_SameCode_OneSuccessOneConflict()
    {
        var (staff, customer, orderId) = await ArrangeConfirmedCustomerOrderAsync();
        var code = await PollTicketCodeAsync(orderId);

        // Hold both scans at the save until each has read the ticket as Issued.
        _factory.Fault.TicketScanGate.Arm(2);
        var first = _client.PostAsAsync(staff, "/api/v1/tickets/validate", new { code });
        var second = _client.PostAsAsync(staff, "/api/v1/tickets/validate", new { code });

        await _factory.Fault.TicketScanGate.WaitForArrivalsAsync(2, TimeSpan.FromSeconds(15));
        _factory.Fault.TicketScanGate.Release();
        var results = await Task.WhenAll(first, second);

        // Exactly one admission; the other must be told the ticket is already used.
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    // ---- Hold quantity is credited at most once under a release race -------------------------

    [Fact]
    public async Task ConcurrentRelease_SameHold_CreditsInventoryOnce()
    {
        // Capacity 10, two holds of 2 (available 6). A SECOND live hold defeats the domain's
        // clamp-at-capacity, so a double credit is visible as availability that is too HIGH.
        var (staff, ticketTypeId, eventId, holdToRelease) = await ArrangeTwoHoldsAsync(capacity: 10, quantity: 2);

        _factory.Fault.HoldReleaseGate.Arm(2);
        var first = _client.PostAsAsync(staff, $"/api/v1/holds/{holdToRelease}/release");
        var second = _client.PostAsAsync(staff, $"/api/v1/holds/{holdToRelease}/release");

        await _factory.Fault.HoldReleaseGate.WaitForArrivalsAsync(2, TimeSpan.FromSeconds(15));
        _factory.Fault.HoldReleaseGate.Release();
        await Task.WhenAll(first, second);

        // Releasing ONE hold of 2 must move availability 6 -> 8 (the other hold still holds 2),
        // never 6 -> 10. A double credit shows up as 10.
        Assert.Equal(8, await AvailabilityAsync(staff, eventId, ticketTypeId));
    }

    // ---- arrange / assert helpers ------------------------------------------------------------

    private async Task<(string Staff, string Customer, Guid OrderId)> ArrangeConfirmedCustomerOrderAsync()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish");
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 30m, currency = "USD", totalQuantity = 10 });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        var customer = await _client.CreateCustomerAsync();
        var holdResponse = await _client.PostAsAsync(customer.Token, "/api/v1/customer/holds",
            new { ticketTypeId = tt.Id, quantity = 1 });
        var hold = (await holdResponse.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;

        var orderResponse = await _client.PostAsAsync(customer.Token, "/api/v1/customer/orders", new { holdId = hold.Id });
        orderResponse.EnsureSuccessStatusCode();
        var order = (await orderResponse.Content.ReadFromJsonAsync<OrderDto>(ApiClientExtensions.Json))!;
        return (staff, customer.Token, order.Id);
    }

    private async Task<(string Staff, Guid TicketTypeId, Guid EventId, Guid HoldToRelease)> ArrangeTwoHoldsAsync(
        int capacity, int quantity)
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 10m, currency = "USD", totalQuantity = capacity });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        var holdA = await CreateStaffHoldAsync(staff, tt.Id, quantity);
        await CreateStaffHoldAsync(staff, tt.Id, quantity); // hold B stays active to defeat the clamp
        return (staff, tt.Id, ev.Id, holdA);
    }

    private async Task<Guid> CreateStaffHoldAsync(string staff, Guid ticketTypeId, int quantity)
    {
        var response = await _client.PostAsAsync(staff, "/api/v1/holds", new { ticketTypeId, quantity });
        response.EnsureSuccessStatusCode();
        var hold = (await response.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;
        return hold.Id;
    }

    private async Task<string> PollTicketCodeAsync(Guid orderId)
    {
        for (var i = 0; i < 80; i++)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            var code = await db.Tickets.IgnoreQueryFilters()
                .Where(t => t.OrderId == orderId).Select(t => t.Code).FirstOrDefaultAsync();
            if (code is not null) return code;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Ticket for order {orderId} was never issued within 20s.");
    }

    private async Task<int> AvailabilityAsync(string staff, Guid eventId, Guid ticketTypeId)
    {
        var graph = await (await _client.GetAsAsync(staff, $"/api/v1/events/{eventId}"))
            .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        return graph!.TicketTypes.Single(t => t.Id == ticketTypeId).AvailableQuantity;
    }

    private async Task<int> OrdersInStatusAsync(Guid orderId, OrderStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        return await db.Orders.IgnoreQueryFilters().CountAsync(o => o.Id == orderId && o.Status == status);
    }
}
