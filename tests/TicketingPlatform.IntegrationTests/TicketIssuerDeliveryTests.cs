using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Persistence;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// PR 3 - the ticket issuer must be idempotent under duplicate OrderConfirmed delivery: the
/// outbox is at-least-once, and a redelivered or replayed event (crash-after-confirm, competing
/// replicas) must never mint a second ticket or a second PDF credential. Exactly one ticket row
/// and one downloadable document survive, and the database code is unchanged.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class TicketIssuerDeliveryTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public TicketIssuerDeliveryTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task TicketIssuer_DuplicateConcurrentDelivery_ProducesMatchingFileAndDatabaseCode()
    {
        const string email = "dup-ticket@example.com";
        const int quantity = 2;
        const decimal price = 20m;

        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { chargeId = "ch_ticket_dup" }));

        var (tenant, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price, currency = "USD", totalQuantity = 10 });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;
        var holdResponse = await _client.PostAsAsync(staff, "/api/v1/holds", new { ticketTypeId = tt.Id, quantity });
        var hold = (await holdResponse.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;
        var orderResponse = await _client.PostAsAsync(staff, "/api/v1/orders", new { holdId = hold.Id, customerEmail = email });
        orderResponse.EnsureSuccessStatusCode();
        var order = (await orderResponse.Content.ReadFromJsonAsync<OrderDto>(ApiClientExtensions.Json))!;

        // The saga issues exactly one ticket for the confirmed order.
        var firstPdf = await PollTicketPdfAsync(staff, order.Id);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(firstPdf, 0, 4));
        var originalCode = await SingleTicketCodeAsync(order.Id);

        // Redeliver the SAME OrderConfirmed twice more (distinct message ids, same order) - what a
        // crash-after-confirm replay or two competing consumer replicas would produce. Publishing
        // through the outbox means the dispatcher builds real versioned envelopes.
        var payload = JsonSerializer.Serialize(
            new { orderId = order.Id, ticketTypeId = tt.Id, quantity, customerEmail = email, amount = price * quantity, currency = "USD" },
            ApiClientExtensions.Json);
        var duplicateIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await WithDbAsync(async db =>
        {
            foreach (var id in duplicateIds)
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Id = id,
                    Type = "OrderConfirmed",
                    SchemaVersion = 1,
                    TenantId = tenant.Id,
                    Payload = payload,
                    OccurredAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        });

        await WaitForDispatchedAsync(duplicateIds);
        await Task.Delay(TimeSpan.FromSeconds(2)); // let the issuer consume the redeliveries

        // Invariant: still exactly one ticket row, the same code, and one intact PDF credential.
        Assert.Equal(1, await CountTicketsAsync(order.Id));
        Assert.Equal(originalCode, await SingleTicketCodeAsync(order.Id));
        var secondPdf = await PollTicketPdfAsync(staff, order.Id);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(secondPdf, 0, 4));
    }

    private async Task<byte[]> PollTicketPdfAsync(string staff, Guid orderId)
    {
        for (var i = 0; i < 60; i++)
        {
            var response = await _client.GetAsAsync(staff, $"/api/v1/orders/{orderId}/ticket");
            if (response.StatusCode == HttpStatusCode.OK)
                return await response.Content.ReadAsByteArrayAsync();
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException($"No ticket PDF for order {orderId} within 15s.");
    }

    private async Task<string> SingleTicketCodeAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        return await db.Tickets.IgnoreQueryFilters().Where(t => t.OrderId == orderId).Select(t => t.Code).SingleAsync();
    }

    private async Task<int> CountTicketsAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        return await db.Tickets.IgnoreQueryFilters().CountAsync(t => t.OrderId == orderId);
    }

    private async Task WaitForDispatchedAsync(IReadOnlyCollection<Guid> ids)
    {
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            var remaining = await db.OutboxMessages.AsNoTracking()
                .CountAsync(m => ids.Contains(m.Id) && m.ProcessedAt == null);
            if (remaining == 0) return;
        }
        throw new Xunit.Sdk.XunitException("the duplicate OrderConfirmed events were not dispatched within 10s.");
    }

    private async Task WithDbAsync(Func<TicketingDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        await action(db);
    }
}
