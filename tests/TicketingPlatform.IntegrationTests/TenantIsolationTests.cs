using System.Net;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The single most important invariant in the system, machine-verified against real Postgres:
/// a tenant can never see or touch another tenant's data, and the API never reveals that a
/// foreign resource exists (404, not 403).
/// </summary>
[Collection(nameof(ApiCollection))]
public class TenantIsolationTests
{
    private readonly HttpClient _client;

    public TenantIsolationTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetById_WithForeignTenantHeader_Returns404()
    {
        var tenantA = await _client.CreateTenantAsync();
        var tenantB = await _client.CreateTenantAsync();
        var ev = await _client.CreateEventAsync(tenantA.Id);

        var response = await _client.GetAsTenantAsync(tenantB.Id, $"/api/v1/events/{ev.Id}");

        // 404, not 403: the global query filter makes the row invisible, so tenant B cannot
        // even learn that the id exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transition_WithForeignTenantHeader_Returns404()
    {
        var tenantA = await _client.CreateTenantAsync();
        var tenantB = await _client.CreateTenantAsync();
        var ev = await _client.CreateEventAsync(tenantA.Id);

        // Writes must be tenant-scoped too - the tracked load honors the same filter.
        var response = await _client.PostAsTenantAsync(tenantB.Id, $"/api/v1/events/{ev.Id}/publish");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_OnlyReturnsOwnTenantsEvents()
    {
        var tenantA = await _client.CreateTenantAsync();
        var tenantB = await _client.CreateTenantAsync();
        await _client.CreateEventAsync(tenantA.Id, "A's event");
        await _client.CreateEventAsync(tenantB.Id, "B's event");

        var response = await _client.GetAsTenantAsync(tenantA.Id, "/api/v1/events?pageSize=100");
        var page = await response.Content.ReadFromJsonAsync<PageDto<EventListItemDto>>(ApiClientExtensions.Json);

        Assert.All(page!.Items, e => Assert.Equal("A's event", e.Name));
    }

    [Fact]
    public async Task Events_WithoutTenantHeader_Returns400Problem()
    {
        var response = await _client.GetAsync("/api/v1/events");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(ApiClientExtensions.Json);
        Assert.Equal("Missing tenant", problem!.Title);
        Assert.NotNull(problem.TraceId); // full RFC 7807 contract, not a bare status
    }
}
