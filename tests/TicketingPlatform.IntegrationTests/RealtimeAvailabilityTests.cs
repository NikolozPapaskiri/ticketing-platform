using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The Phase 7 real-time pair, end to end against real Postgres + RabbitMQ + Redis:
///  - CQRS projection: AvailabilityChanged events (hold create/release) flow through the
///    outbox and broker into the denormalized read model served by /events/{id}/availability.
///  - SignalR push: a connected client in the event's group receives "availabilityChanged"
///    through the hub (and the Redis backplane, which also proves MessagePack 3 compat).
/// Everything here is eventually consistent by design, so assertions poll with timeouts.
/// </summary>
[Collection(nameof(ApiCollection))]
public class RealtimeAvailabilityTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public RealtimeAvailabilityTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(string Staff, EventDto Event, TicketTypeDto TicketType)> SetupAsync(int capacity)
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var response = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 10m, currency = "USD", totalQuantity = capacity });
        var tt = (await response.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;
        return (staff, ev, tt);
    }

    private async Task<int?> ReadModelAvailabilityAsync(string staff, Guid eventId, Guid ticketTypeId)
    {
        var response = await _client.GetAsAsync(staff, $"/api/v1/events/{eventId}/availability");
        var rows = await response.Content.ReadFromJsonAsync<List<AvailabilityRow>>(ApiClientExtensions.Json);
        return rows?.FirstOrDefault(r => r.TicketTypeId == ticketTypeId)?.Available;
    }

    [Fact]
    public async Task Projection_UpdatesReadModel_OnHoldAndRelease()
    {
        var (staff, ev, tt) = await SetupAsync(capacity: 10);

        var holdResponse = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = tt.Id, quantity = 3 });
        var hold = await holdResponse.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json);

        // Eventually consistent: outbox -> dispatcher -> broker -> projection -> read model.
        var value = await PollAsync(() => ReadModelAvailabilityAsync(staff, ev.Id, tt.Id), v => v == 7);
        Assert.Equal(7, value);

        await _client.PostAsAsync(staff, $"/api/v1/holds/{hold!.Id}/release");
        value = await PollAsync(() => ReadModelAvailabilityAsync(staff, ev.Id, tt.Id), v => v == 10);
        Assert.Equal(10, value);
    }

    [Fact]
    public async Task SignalR_PushesAvailability_ToEventGroup()
    {
        var (staff, ev, tt) = await SetupAsync(capacity: 20);

        // Connect exactly like a browser would (over the in-memory test server).
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/availability"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling; // TestServer has no websockets
            })
            .Build();

        var received = new TaskCompletionSource<AvailabilityPush>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<AvailabilityPush>("availabilityChanged", push => received.TrySetResult(push));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinEvent", ev.Id);

        await _client.PostAsAsync(staff, "/api/v1/holds", new { ticketTypeId = tt.Id, quantity = 5 });

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(completed == received.Task, "no availabilityChanged push within 15s - hub/backplane/projection chain broken");
        var push = await received.Task;
        Assert.Equal(tt.Id, push.TicketTypeId);
        Assert.Equal(15, push.Available);
    }

    [Fact]
    public async Task Cors_IsScopedToAvailabilityHub()
    {
        var hubPreflight = new HttpRequestMessage(HttpMethod.Options, "/hubs/availability/negotiate?negotiateVersion=1");
        hubPreflight.Headers.Add("Origin", "http://localhost:3000");
        hubPreflight.Headers.Add("Access-Control-Request-Method", "POST");
        hubPreflight.Headers.Add("Access-Control-Request-Headers", "content-type");

        var hubResponse = await _client.SendAsync(hubPreflight);

        Assert.True(hubResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out var hubOrigins));
        Assert.Contains("http://localhost:3000", hubOrigins);

        var restRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/public/tenants");
        restRequest.Headers.Add("Origin", "http://localhost:3000");

        var restResponse = await _client.SendAsync(restRequest);

        Assert.False(restResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static async Task<int?> PollAsync(Func<Task<int?>> read, Func<int?, bool> done)
    {
        int? value = null;
        for (var i = 0; i < 60; i++)
        {
            value = await read();
            if (done(value)) return value;
            await Task.Delay(250);
        }
        return value;
    }

    private sealed record AvailabilityRow(Guid TicketTypeId, string TicketTypeName, int Available, int Total, DateTimeOffset UpdatedAt);
    private sealed record AvailabilityPush(Guid TicketTypeId, int Available);
}
