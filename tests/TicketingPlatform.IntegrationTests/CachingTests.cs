using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Cache-aside on the event graph against real Redis. Cache-hit proof: mutate the row directly
/// in the database (behind the API's back) and show the API still serves the cached value;
/// then a transition invalidates and the fresh row comes through.
/// </summary>
[Collection(nameof(ApiCollection))]
public class CachingTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public CachingTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task EventGraph_IsServedFromCache_AndInvalidatedOnTransition()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff, "Original Name");

        // Prime the cache.
        var first = await (await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}"))
            .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        Assert.Equal("Original Name", first!.Name);

        // Rename the row directly in Postgres, bypassing the API (and its invalidation).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            // IgnoreQueryFilters: this scope has no tenant context, so the filter would hide the row.
            var entity = await db.Events.IgnoreQueryFilters().SingleAsync(e => e.Id == ev.Id);
            db.Entry(entity).Property("Name").CurrentValue = "Renamed Behind The Api";
            await db.SaveChangesAsync();
        }

        // Cache HIT proof: the API still serves the pre-rename value.
        var cachedRead = await (await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}"))
            .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        Assert.Equal("Original Name", cachedRead!.Name);

        // A transition invalidates the key: the next read comes from the DB and sees everything fresh.
        await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish");
        var freshRead = await (await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}"))
            .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        Assert.Equal("Renamed Behind The Api", freshRead!.Name);
        Assert.Equal("OnSale", freshRead.Status);
    }

    [Fact]
    public async Task HoldOperations_InvalidateTheEventGraph_ReadYourWrites()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 10m, currency = "USD", totalQuantity = 10 });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        // Prime the cache, then hold: the staff member must immediately see 7, not a stale 10.
        await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}");
        await _client.PostAsAsync(staff, "/api/v1/holds", new { ticketTypeId = tt.Id, quantity = 3 });

        var afterHold = await (await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}"))
            .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        Assert.Equal(7, afterHold!.TicketTypes.Single().AvailableQuantity);
    }

    [Fact]
    public async Task CachedGraph_IsNotVisibleToOtherTenants()
    {
        // The cache key includes the tenant id: even a cached row must be a MISS for tenant B.
        var (_, staffA) = await _client.CreateTenantWithStaffAsync();
        var (_, staffB) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staffA);

        await _client.GetAsAsync(staffA, $"/api/v1/events/{ev.Id}"); // primes tenant A's cache entry

        var response = await _client.GetAsAsync(staffB, $"/api/v1/events/{ev.Id}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
