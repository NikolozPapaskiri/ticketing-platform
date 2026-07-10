using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Infrastructure.Persistence;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace TicketingPlatform.IntegrationTests;

[Collection(nameof(ApiCollection))]
public sealed class CustomerCheckoutTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public CustomerCheckoutTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CustomerCheckout_IsOwned_Idempotent_Refundable_AndTicketCanBeValidated()
    {
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { chargeId = "ch_customer" }));
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/refunds").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { refundId = "rf_customer" }));

        var (tenant, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish");
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 30m, currency = "USD", totalQuantity = 20 });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        var publicCatalog = await _client.GetAsync($"/api/v1/public/tenants/{tenant.Slug}/events");
        Assert.Equal(HttpStatusCode.OK, publicCatalog.StatusCode);
        var publicPage = await publicCatalog.Content.ReadFromJsonAsync<PageDto<EventListItemDto>>(ApiClientExtensions.Json);
        Assert.Contains(publicPage!.Items, e => e.Id == ev.Id);

        var customer = await _client.CreateCustomerAsync();
        var holdResponse = await _client.PostAsAsync(customer.Token, "/api/v1/customer/holds",
            new { ticketTypeId = tt.Id, quantity = 2 });
        Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);
        var hold = (await holdResponse.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;

        var holdsResponse = await _client.GetAsAsync(customer.Token, "/api/v1/customer/holds");
        Assert.Equal(HttpStatusCode.OK, holdsResponse.StatusCode);
        var holds = await holdsResponse.Content.ReadFromJsonAsync<List<HoldDto>>(ApiClientExtensions.Json);
        Assert.Contains(holds!, h => h.Id == hold.Id);

        var firstOrderResponse = await PostAsAsync(customer.Token, "/api/v1/customer/orders",
            new { holdId = hold.Id }, idempotencyKey: "customer-order-1");
        Assert.Equal(HttpStatusCode.Created, firstOrderResponse.StatusCode);
        var firstOrder = (await firstOrderResponse.Content.ReadFromJsonAsync<OrderDto>(ApiClientExtensions.Json))!;
        Assert.Equal("Confirmed", firstOrder.Status);

        var duplicateOrderResponse = await PostAsAsync(customer.Token, "/api/v1/customer/orders",
            new { holdId = hold.Id }, idempotencyKey: "customer-order-1");
        Assert.Equal(HttpStatusCode.Created, duplicateOrderResponse.StatusCode);
        var duplicateOrder = (await duplicateOrderResponse.Content.ReadFromJsonAsync<OrderDto>(ApiClientExtensions.Json))!;
        Assert.Equal(firstOrder.Id, duplicateOrder.Id);

        var otherCustomer = await _client.CreateCustomerAsync();
        var foreignRead = await _client.GetAsAsync(otherCustomer.Token, $"/api/v1/customer/orders/{firstOrder.Id}");
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);

        var ordersResponse = await _client.GetAsAsync(customer.Token, "/api/v1/customer/orders");
        Assert.Equal(HttpStatusCode.OK, ordersResponse.StatusCode);
        var orders = await ordersResponse.Content.ReadFromJsonAsync<List<OrderDto>>(ApiClientExtensions.Json);
        Assert.Contains(orders!, o => o.Id == firstOrder.Id);

        var foreignOrdersResponse = await _client.GetAsAsync(otherCustomer.Token, "/api/v1/customer/orders");
        Assert.Equal(HttpStatusCode.OK, foreignOrdersResponse.StatusCode);
        var foreignOrders = await foreignOrdersResponse.Content.ReadFromJsonAsync<List<OrderDto>>(ApiClientExtensions.Json);
        Assert.DoesNotContain(foreignOrders!, o => o.Id == firstOrder.Id);

        var foreignHoldsResponse = await _client.GetAsAsync(otherCustomer.Token, "/api/v1/customer/holds");
        Assert.Equal(HttpStatusCode.OK, foreignHoldsResponse.StatusCode);
        var foreignHolds = await foreignHoldsResponse.Content.ReadFromJsonAsync<List<HoldDto>>(ApiClientExtensions.Json);
        Assert.DoesNotContain(foreignHolds!, h => h.Id == hold.Id);

        var ticketDownload = await PollTicketAsync(customer.Token, firstOrder.Id);
        Assert.Equal(HttpStatusCode.OK, ticketDownload.StatusCode);

        string code;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            code = (await db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.OrderId == firstOrder.Id)).Code;
        }

        var scan = await _client.PostAsAsync(staff, "/api/v1/tickets/validate", new { code });
        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        var doubleScan = await _client.PostAsAsync(staff, "/api/v1/tickets/validate", new { code });
        Assert.Equal(HttpStatusCode.Conflict, doubleScan.StatusCode);

        // Policy (docs/ARCHITECTURE.md): a scanned ticket is a consumed good - non-refundable.
        var refundAfterScan = await _client.PostAsAsync(customer.Token, $"/api/v1/customer/orders/{firstOrder.Id}/refund");
        Assert.Equal(HttpStatusCode.Conflict, refundAfterScan.StatusCode);
    }

    private async Task<HttpResponseMessage> PollTicketAsync(string token, Guid orderId)
    {
        HttpResponseMessage? response = null;
        for (var i = 0; i < 60; i++)
        {
            response = await _client.GetAsAsync(token, $"/api/v1/customer/orders/{orderId}/ticket");
            if (response.StatusCode == HttpStatusCode.OK) return response;
            await Task.Delay(250);
        }
        return response!;
    }

    private Task<HttpResponseMessage> PostAsAsync(
        string token, string url, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(body);
        return _client.SendAsync(request);
    }
}
