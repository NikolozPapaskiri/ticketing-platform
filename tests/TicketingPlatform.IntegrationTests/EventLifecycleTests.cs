using System.Net;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

[Collection(nameof(ApiCollection))]
public class EventLifecycleTests
{
    private readonly HttpClient _client;

    public EventLifecycleTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task FullFlow_CreateEvent_AddTicketType_GetGraph()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        Assert.Equal("Draft", ev.Status); // new events always start Draft - the entity owns that

        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 49.90m, currency = "USD", totalQuantity = 100 });
        Assert.Equal(HttpStatusCode.OK, ttResponse.StatusCode);

        var getResponse = await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}");
        var graph = await getResponse.Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);

        var tt = Assert.Single(graph!.TicketTypes);
        Assert.Equal(100, tt.TotalQuantity);
        Assert.Equal(100, tt.AvailableQuantity); // inventory seeded 1:1 with capacity
    }

    [Fact]
    public async Task Transitions_FollowStateMachine_IllegalMovesReturn409()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);

        // Draft -> OnSale
        var publish = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent, publish.StatusCode);

        // OnSale -> OnSale is illegal
        var republish = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish");
        Assert.Equal(HttpStatusCode.Conflict, republish.StatusCode);
        var problem = await republish.Content.ReadFromJsonAsync<ProblemDto>(ApiClientExtensions.Json);
        Assert.Equal("Illegal status transition", problem!.Title);
        Assert.NotNull(problem.TraceId);

        // OnSale -> Closed, then Closed is terminal
        var close = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/close");
        Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
        var reclose = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/close");
        Assert.Equal(HttpStatusCode.Conflict, reclose.StatusCode);
    }

    [Fact]
    public async Task Transition_UnknownEvent_Returns404()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();

        var response = await _client.PostAsAsync(staff, $"/api/v1/events/{Guid.NewGuid()}/publish");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_Paginates_WithStableTotals()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        for (var i = 0; i < 3; i++)
            await _client.CreateEventAsync(staff, $"Event {i}");

        var response = await _client.GetAsAsync(staff, "/api/v1/events?page=1&pageSize=2");
        var page = await response.Content.ReadFromJsonAsync<PageDto<EventListItemDto>>(ApiClientExtensions.Json);

        Assert.Equal(2, page!.Items.Count);
        Assert.Equal(3, page.TotalItems);
        Assert.Equal(2, page.TotalPages);
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff, "Published one");
        await _client.CreateEventAsync(staff, "Draft one");
        await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish");

        var response = await _client.GetAsAsync(staff, "/api/v1/events?status=OnSale");
        var page = await response.Content.ReadFromJsonAsync<PageDto<EventListItemDto>>(ApiClientExtensions.Json);

        var item = Assert.Single(page!.Items);
        Assert.Equal("Published one", item.Name);
        Assert.Equal("OnSale", item.Status);
    }

    [Fact]
    public async Task List_PageZero_Returns400()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();

        var response = await _client.GetAsAsync(staff, "/api/v1/events?page=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateEvent_InvalidBody_Returns400WithFieldErrors()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();

        // Empty name + past start date: both rules must appear in the validation problem.
        var response = await _client.PostAsAsync(staff, "/api/v1/events",
            new { name = "", startsAt = DateTimeOffset.UtcNow.AddDays(-1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Name", body);
        Assert.Contains("StartsAt", body);
    }
}
