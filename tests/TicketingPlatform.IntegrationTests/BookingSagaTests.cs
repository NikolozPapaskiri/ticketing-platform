using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Infrastructure.Persistence;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The booking saga end to end against real Postgres, Redis, RabbitMQ, and a scriptable
/// payment provider: hold -> charge -> confirm -> outbox -> RabbitMQ -> consumer -> notification.
/// </summary>
[Collection(nameof(ApiCollection))]
public class BookingSagaTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public BookingSagaTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(string Staff, EventDto Event, HoldDto Hold)> SetupHoldAsync(int quantity = 2)
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 25m, currency = "USD", totalQuantity = 10 });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;
        var holdResponse = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = tt.Id, quantity });
        var hold = (await holdResponse.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;
        return (staff, ev, hold);
    }

    private void StubPaymentSuccess()
    {
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { chargeId = "ch_saga" }));
    }

    [Fact]
    public async Task HappyPath_OrderConfirmed_HoldConverted_NotificationEventuallyWritten()
    {
        StubPaymentSuccess();
        var (staff, _, hold) = await SetupHoldAsync(quantity: 2);

        var response = await _client.PostAsAsync(staff, "/api/v1/orders",
            new { holdId = hold.Id, customerEmail = "buyer@example.com" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderDto>(ApiClientExtensions.Json);
        Assert.Equal("Confirmed", order!.Status);
        Assert.Equal(50m, order.Amount); // 2 x 25.00, priced server-side from the hold

        // The hold converted to a sale.
        var holdRead = await (await _client.GetAsAsync(staff, $"/api/v1/holds/{hold.Id}"))
            .Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json);
        Assert.Equal("Confirmed", holdRead!.Status);

        // And the async half of the saga: outbox -> dispatcher -> RabbitMQ -> consumer ->
        // Notification row. Eventually consistent, so poll with a timeout.
        var found = false;
        for (var i = 0; i < 60 && !found; i++)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            found = await db.Notifications.IgnoreQueryFilters()
                .AnyAsync(n => n.Message.Contains(order.Id.ToString()));
            if (!found) await Task.Delay(250);
        }
        Assert.True(found, "OrderConfirmed never produced a Notification within 15s - outbox/broker/consumer chain broken");
    }

    [Fact]
    public async Task PaymentDeclined_OrderFails_HoldSurvivesForRetry()
    {
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(402));
        var (staff, _, hold) = await SetupHoldAsync();

        var declined = await _client.PostAsAsync(staff, "/api/v1/orders",
            new { holdId = hold.Id, customerEmail = "buyer@example.com" });
        Assert.Equal(HttpStatusCode.Conflict, declined.StatusCode);

        // Compensation semantics: the hold STAYS ACTIVE (retry window until TTL).
        var holdRead = await (await _client.GetAsAsync(staff, $"/api/v1/holds/{hold.Id}"))
            .Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json);
        Assert.Equal("Active", holdRead!.Status);

        // The buyer retries with a working card and succeeds on the SAME hold.
        StubPaymentSuccess();
        var retried = await _client.PostAsAsync(staff, "/api/v1/orders",
            new { holdId = hold.Id, customerEmail = "buyer@example.com" });
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }

    [Fact]
    public async Task ProviderDown_Returns503_NothingRecorded()
    {
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));
        var (staff, _, hold) = await SetupHoldAsync();

        var response = await _client.PostAsAsync(staff, "/api/v1/orders",
            new { holdId = hold.Id, customerEmail = "buyer@example.com" });

        // An outage is not a decline: 503, the hold untouched, the buyer just retries.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var holdRead = await (await _client.GetAsAsync(staff, $"/api/v1/holds/{hold.Id}"))
            .Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json);
        Assert.Equal("Active", holdRead!.Status);
    }

    [Fact]
    public async Task ConfirmedOrder_EventuallyGetsATicketPdf()
    {
        StubPaymentSuccess();
        var (staff, _, hold) = await SetupHoldAsync(quantity: 1);
        var orderResponse = await _client.PostAsAsync(staff, "/api/v1/orders",
            new { holdId = hold.Id, customerEmail = "pdf-buyer@example.com" });
        var order = await orderResponse.Content.ReadFromJsonAsync<OrderDto>(ApiClientExtensions.Json);

        // Issued asynchronously (OrderConfirmed -> ticket-issuer consumer), so poll.
        HttpResponseMessage? ticket = null;
        for (var i = 0; i < 60; i++)
        {
            ticket = await _client.GetAsAsync(staff, $"/api/v1/orders/{order!.Id}/ticket");
            if (ticket.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(250);
        }

        Assert.Equal(HttpStatusCode.OK, ticket!.StatusCode);
        Assert.Equal("application/pdf", ticket.Content.Headers.ContentType!.MediaType);
        var bytes = await ticket.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 500, "suspiciously small ticket document");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4)); // magic header
    }

    [Fact]
    public async Task ReleasedHold_CannotBePurchased()
    {
        StubPaymentSuccess();
        var (staff, _, hold) = await SetupHoldAsync();
        await _client.PostAsAsync(staff, $"/api/v1/holds/{hold.Id}/release");

        var response = await _client.PostAsAsync(staff, "/api/v1/orders",
            new { holdId = hold.Id, customerEmail = "buyer@example.com" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}

internal sealed record OrderDto(Guid Id, Guid HoldId, string CustomerEmail, decimal Amount, string Currency, string Status);
