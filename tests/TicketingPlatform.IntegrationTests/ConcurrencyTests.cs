using System.Net;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// THE marquee test of the project: many concurrent buyers, few tickets, and the system must
/// never sell more than exist. A naive read-check-write here would oversell (two buyers both
/// read "1 left", both decrement); the active IReservationStrategy (optimistic xmin by
/// default) must make one of them lose.
/// </summary>
[Collection(nameof(ApiCollection))]
public class ConcurrencyTests
{
    private readonly HttpClient _client;

    public ConcurrencyTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task ConcurrentBuyers_NeverOversell()
    {
        const int capacity = 10;
        const int buyers = 30;

        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 10m, currency = "USD", totalQuantity = capacity });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        // 30 buyers fire at once for 10 tickets. Task.WhenAll makes the contention real:
        // these requests genuinely overlap inside the API and the database.
        var attempts = await Task.WhenAll(Enumerable.Range(0, buyers).Select(_ =>
            _client.PostAsAsync(staff, "/api/v1/holds", new { ticketTypeId = tt.Id, quantity = 1 })));

        var won = attempts.Count(r => r.StatusCode == HttpStatusCode.Created);
        var lost = attempts.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        // Every request got a definitive answer - no 500s under contention.
        Assert.Equal(buyers, won + lost);

        // THE invariant: never more sales than capacity...
        Assert.True(won <= capacity, $"OVERSOLD: {won} holds created for {capacity} tickets");
        // ...and the strategy actually sold something under contention.
        Assert.True(won > 0, "no holds succeeded at all - the strategy is failing closed");

        // The books balance exactly: availability + wins == capacity, and never negative.
        var graph = await (await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}"))
            .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        var remaining = graph!.TicketTypes.Single().AvailableQuantity;
        Assert.True(remaining >= 0, $"availability went NEGATIVE: {remaining}");
        Assert.Equal(capacity, remaining + won);
    }

    [Fact]
    public async Task ConcurrentBuyers_ExactCapacity_SellsOutCleanly()
    {
        // Lower contention (2 buyers per ticket): the optimistic strategy's retries should
        // sell the room out completely - the wasted-work cost only explodes at extreme ratios.
        const int capacity = 5;

        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 10m, currency = "USD", totalQuantity = capacity });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        // Sequential-ish waves of concurrent pairs keep the conflict rate inside retry budget.
        var totalWon = 0;
        for (var wave = 0; wave < 5 && totalWon < capacity; wave++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                _client.PostAsAsync(staff, "/api/v1/holds", new { ticketTypeId = tt.Id, quantity = 1 })));
            totalWon += results.Count(r => r.StatusCode == HttpStatusCode.Created);
        }

        Assert.Equal(capacity, totalWon); // sold out exactly, never beyond

        var graph = await (await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}"))
            .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        Assert.Equal(0, graph!.TicketTypes.Single().AvailableQuantity);
    }
}
