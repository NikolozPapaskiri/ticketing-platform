using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Short payment lease + fast reconcile scan so the background reconciler acts within a test.
/// </summary>
public sealed class PaymentReconcilerApiFactory : PaymentRaceApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Holds:PaymentLeaseSeconds", "1");  // an in-flight attempt is "orphaned" after 1s
        builder.UseSetting("Holds:ReconcileScanSeconds", "1"); // ...and the reconciler scans every second
    }
}

/// <summary>
/// The self-healing arm of the payment saga: a checkout that charged then crashed, with the
/// client NEVER retrying, must still be completed by the background reconciler - querying the
/// provider (not charging again) and confirming the original order once its lease expires.
/// </summary>
[Collection(nameof(PaymentReconcilerCollection))]
public sealed class PaymentReconciliationTests
{
    private readonly PaymentReconcilerApiFactory _factory;
    private readonly HttpClient _client;

    public PaymentReconciliationTests(PaymentReconcilerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.Gateway.Reset();
        _factory.Gateway.ChargeGate.Arm(0);
        _factory.Fault.Reset();
        _factory.Fault.IdempotencyClaimGate.Arm(0);
    }

    [Fact]
    public async Task Reconciler_CompletesOrphanedOrder_WithoutChargingAgain()
    {
        var (customerToken, holdId) = await ArrangeOnSaleHoldAsync();

        // Charge succeeds, then the confirmation commit crashes: the order is stranded in
        // PendingPayment with the money already taken. The client does NOT retry.
        _factory.Fault.FailNextOrderConfirmSave = true;
        var crashed = await PostOrderAsync(customerToken, holdId, "reconcile-me");
        Assert.Equal(HttpStatusCode.InternalServerError, crashed.StatusCode);
        Assert.Equal(1, _factory.Gateway.DistinctChargeCount);

        // The reconciler (lease 1s, scan 1s) must find the orphaned lease, ask the provider,
        // and confirm the original order on its own.
        var confirmed = await PollAsync(async () => await ConfirmedOrdersForHoldAsync(holdId) == 1,
            timeout: TimeSpan.FromSeconds(20));
        Assert.True(confirmed, "the reconciler never completed the orphaned PendingPayment order within 20s");
        Assert.Equal(1, _factory.Gateway.DistinctChargeCount); // reconciliation queries status, never re-charges
    }

    private async Task<(string CustomerToken, Guid HoldId)> ArrangeOnSaleHoldAsync()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish");
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 20m, currency = "USD", totalQuantity = 5 });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        var customer = await _client.CreateCustomerAsync();
        var holdResponse = await _client.PostAsAsync(customer.Token, "/api/v1/customer/holds",
            new { ticketTypeId = tt.Id, quantity = 1 });
        holdResponse.EnsureSuccessStatusCode();
        var hold = (await holdResponse.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;
        return (customer.Token, hold.Id);
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

    private async Task<int> ConfirmedOrdersForHoldAsync(Guid holdId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        return await db.Orders.IgnoreQueryFilters()
            .CountAsync(o => o.HoldId == holdId && o.Status == OrderStatus.Confirmed);
    }

    private static async Task<bool> PollAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(500);
        }
        return false;
    }
}

[CollectionDefinition(nameof(PaymentReconcilerCollection))]
public sealed class PaymentReconcilerCollection : ICollectionFixture<PaymentReconcilerApiFactory>;
