using System.Net;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The single most important invariant, machine-verified against real Postgres: a tenant's
/// staff can never see or touch another tenant's data - even with a perfectly valid token -
/// and the API never reveals that a foreign resource exists (404, not 403).
/// Since Phase 3 the tenant comes from the signed tenant_id claim, so these tests each hold
/// real staff tokens for two different tenants.
/// </summary>
[Collection(nameof(ApiCollection))]
public class TenantIsolationTests
{
    private readonly HttpClient _client;

    public TenantIsolationTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetById_WithForeignStaffToken_Returns404()
    {
        var (_, staffA) = await _client.CreateTenantWithStaffAsync();
        var (_, staffB) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staffA);

        var response = await _client.GetAsAsync(staffB, $"/api/v1/events/{ev.Id}");

        // 404, not 403: the global query filter makes the row invisible, so tenant B cannot
        // even learn that the id exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transition_WithForeignStaffToken_Returns404()
    {
        var (_, staffA) = await _client.CreateTenantWithStaffAsync();
        var (_, staffB) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staffA);

        // Writes must be tenant-scoped too - the tracked load honors the same filter.
        var response = await _client.PostAsAsync(staffB, $"/api/v1/events/{ev.Id}/publish");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_OnlyReturnsOwnTenantsEvents()
    {
        var (_, staffA) = await _client.CreateTenantWithStaffAsync();
        var (_, staffB) = await _client.CreateTenantWithStaffAsync();
        await _client.CreateEventAsync(staffA, "A's event");
        await _client.CreateEventAsync(staffB, "B's event");

        var response = await _client.GetAsAsync(staffA, "/api/v1/events?pageSize=100");
        var page = await response.Content.ReadFromJsonAsync<PageDto<EventListItemDto>>(ApiClientExtensions.Json);

        Assert.All(page!.Items, e => Assert.Equal("A's event", e.Name));
    }

    [Fact]
    public async Task Events_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
