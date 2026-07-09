using System.Net;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The reserve/release flow against real Postgres: a hold decrements availability atomically
/// with its own creation, and releasing gives the quantity back exactly once.
/// </summary>
[Collection(nameof(ApiCollection))]
public class HoldTests
{
    private readonly HttpClient _client;

    public HoldTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    private async Task<(string Staff, EventDto Event, TicketTypeDto TicketType)> SetupInventoryAsync(int capacity)
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var response = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 10m, currency = "USD", totalQuantity = capacity });
        var tt = (await response.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;
        return (staff, ev, tt);
    }

    private async Task<int> AvailabilityAsync(string staff, Guid eventId)
    {
        var response = await _client.GetAsAsync(staff, $"/api/v1/events/{eventId}");
        var graph = await response.Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        return graph!.TicketTypes.Single().AvailableQuantity;
    }

    [Fact]
    public async Task CreateHold_DecrementsAvailability()
    {
        var (staff, ev, tt) = await SetupInventoryAsync(capacity: 50);

        var response = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = tt.Id, quantity = 8 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var hold = await response.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json);
        Assert.Equal("Active", hold!.Status);
        Assert.True(hold.ExpiresAt > DateTimeOffset.UtcNow); // TTL stamped
        Assert.Equal(42, await AvailabilityAsync(staff, ev.Id));
    }

    [Fact]
    public async Task CreateHold_MoreThanAvailable_Returns409_AndStockUntouched()
    {
        var (staff, ev, tt) = await SetupInventoryAsync(capacity: 5);

        var response = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = tt.Id, quantity = 6 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(ApiClientExtensions.Json);
        Assert.Equal("Insufficient availability", problem!.Title);
        Assert.Contains("only 5 available", problem.Detail);
        Assert.Equal(5, await AvailabilityAsync(staff, ev.Id)); // rejected reserve is a no-op
    }

    [Fact]
    public async Task ReleaseHold_RestoresAvailability_ExactlyOnce()
    {
        var (staff, ev, tt) = await SetupInventoryAsync(capacity: 20);
        var create = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = tt.Id, quantity = 4 });
        var hold = await create.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json);
        Assert.Equal(16, await AvailabilityAsync(staff, ev.Id));

        var release = await _client.PostAsAsync(staff, $"/api/v1/holds/{hold!.Id}/release");
        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
        Assert.Equal(20, await AvailabilityAsync(staff, ev.Id));

        // Releasing again is a state conflict and must not mint extra stock.
        var again = await _client.PostAsAsync(staff, $"/api/v1/holds/{hold.Id}/release");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(20, await AvailabilityAsync(staff, ev.Id));
    }

    [Fact]
    public async Task CreateHold_UnknownTicketType_Returns404()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();

        var response = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = Guid.NewGuid(), quantity = 1 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateHold_ForeignTenantsTicketType_Returns404()
    {
        var (_, _, tt) = await SetupInventoryAsync(capacity: 10);
        var (_, staffB) = await _client.CreateTenantWithStaffAsync();

        // Tenant B's staff cannot reserve tenant A's inventory - invisible, so 404 (not 403).
        var response = await _client.PostAsAsync(staffB, "/api/v1/holds",
            new { ticketTypeId = tt.Id, quantity = 1 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateHold_ZeroQuantity_Returns400Validation()
    {
        var (staff, _, tt) = await SetupInventoryAsync(capacity: 10);

        var response = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = tt.Id, quantity = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

internal sealed record HoldDto(Guid Id, Guid TicketTypeId, int Quantity, string Status, DateTimeOffset ExpiresAt);
