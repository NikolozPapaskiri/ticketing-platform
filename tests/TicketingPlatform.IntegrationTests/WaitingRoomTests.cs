using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The virtual waiting room end to end against real Redis: FIFO admission through the
/// background valve, API-level enforcement (an unadmitted customer gets 429 no matter what
/// the UI shows), the staff bypass, and the SignalR "queueAdmitted" push. The test factory
/// runs the valve fast (batch 1, 1s tick) so admission is observable within seconds.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class WaitingRoomTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public WaitingRoomTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(string Staff, TicketTypeDto TicketType, Guid EventId)> SetupOnSaleEventAsync(bool waitingRoom)
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff, waitingRoomEnabled: waitingRoom);
        await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish");
        var response = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 15m, currency = "USD", totalQuantity = 100 });
        var tt = (await response.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;
        return (staff, tt, ev.Id);
    }

    private async Task<QueueStatusDto> JoinQueueAsync(Guid eventId, Guid visitorId)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/public/events/{eventId}/queue", new { visitorId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QueueStatusDto>(ApiClientExtensions.Json))!;
    }

    private async Task<QueueStatusDto> GetQueueStatusAsync(Guid eventId, Guid visitorId)
    {
        var response = await _client.GetAsync($"/api/v1/public/events/{eventId}/queue/{visitorId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QueueStatusDto>(ApiClientExtensions.Json))!;
    }

    private async Task<QueueStatusDto> PollUntilAdmittedAsync(Guid eventId, Guid visitorId, int timeoutSeconds = 20)
    {
        QueueStatusDto status = await GetQueueStatusAsync(eventId, visitorId);
        for (var i = 0; i < timeoutSeconds * 2 && !status.Admitted; i++)
        {
            await Task.Delay(500);
            status = await GetQueueStatusAsync(eventId, visitorId);
        }
        return status;
    }

    private Task<HttpResponseMessage> CreateCustomerHoldAsync(string customerToken, Guid ticketTypeId, Guid? visitorId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/customer/holds")
        {
            Content = JsonContent.Create(new { ticketTypeId, quantity = 1 })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
        if (visitorId is not null)
            request.Headers.Add("X-Visitor-Id", visitorId.Value.ToString());
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task UnadmittedCustomer_Gets429_AdmittedCustomer_CanReserve()
    {
        var (_, tt, eventId) = await SetupOnSaleEventAsync(waitingRoom: true);
        var customer = await _client.CreateCustomerAsync();

        // No visitor header at all: the API says "get in line", regardless of what the UI hid.
        var blocked = await CreateCustomerHoldAsync(customer.Token, tt.Id, visitorId: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // A visitor id that never joined the queue is blocked too.
        var stranger = await CreateCustomerHoldAsync(customer.Token, tt.Id, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.TooManyRequests, stranger.StatusCode);

        // Join, wait for the valve to admit, then reserve with the same visitor id.
        var visitorId = Guid.NewGuid();
        await JoinQueueAsync(eventId, visitorId);
        var status = await PollUntilAdmittedAsync(eventId, visitorId);
        Assert.True(status.Admitted, "visitor was not admitted within the timeout - admitter valve broken");

        var allowed = await CreateCustomerHoldAsync(customer.Token, tt.Id, visitorId);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Queue_IsFifo_AndJoinIsIdempotent()
    {
        var (_, _, eventId) = await SetupOnSaleEventAsync(waitingRoom: true);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var firstJoin = await JoinQueueAsync(eventId, first);
        var secondJoin = await JoinQueueAsync(eventId, second);

        // Positions are 1-based arrival order (unless the fast test valve already admitted #1).
        if (!firstJoin.Admitted && !secondJoin.Admitted)
            Assert.True(firstJoin.Position < secondJoin.Position,
                $"expected FIFO, got first={firstJoin.Position} second={secondJoin.Position}");

        // Rejoining must keep the original spot, not push the visitor to the back.
        var rejoin = await JoinQueueAsync(eventId, second);
        Assert.True(rejoin.Admitted || rejoin.Position <= secondJoin.Position);

        // FIFO admission: whenever the later visitor is through, the earlier one must be too.
        var secondStatus = await PollUntilAdmittedAsync(eventId, second);
        Assert.True(secondStatus.Admitted);
        var firstStatus = await GetQueueStatusAsync(eventId, first);
        Assert.True(firstStatus.Admitted, "second joiner admitted before the first - FIFO broken");
    }

    [Fact]
    public async Task StaffAndBoxOffice_BypassTheWaitingRoom()
    {
        var (staff, tt, _) = await SetupOnSaleEventAsync(waitingRoom: true);

        // The staff hold endpoint is the box-office path: no queue, straight to reserve.
        var response = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = tt.Id, quantity = 2 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task EventWithoutWaitingRoom_AdmitsTrivially_AndHoldsNeedNoHeader()
    {
        var (_, tt, eventId) = await SetupOnSaleEventAsync(waitingRoom: false);

        var join = await JoinQueueAsync(eventId, Guid.NewGuid());
        Assert.True(join.Admitted);
        Assert.Equal(0, join.Position);

        var customer = await _client.CreateCustomerAsync();
        var hold = await CreateCustomerHoldAsync(customer.Token, tt.Id, visitorId: null);
        Assert.Equal(HttpStatusCode.Created, hold.StatusCode);
    }

    [Fact]
    public async Task QueueEndpoints_Return404_ForUnknownOrOffSaleEvents()
    {
        var unknown = await _client.PostAsJsonAsync($"/api/v1/public/events/{Guid.NewGuid()}/queue",
            new { visitorId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Draft event (never published): the queue must not exist for it either.
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var draft = await _client.CreateEventAsync(staff, waitingRoomEnabled: true);
        var offSale = await _client.PostAsJsonAsync($"/api/v1/public/events/{draft.Id}/queue",
            new { visitorId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, offSale.StatusCode);
    }

    [Fact]
    public async Task SignalR_PushesQueueAdmitted_ToTheVisitorGroup()
    {
        var (_, _, eventId) = await SetupOnSaleEventAsync(waitingRoom: true);
        var visitorId = Guid.NewGuid();

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/availability"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling; // TestServer has no websockets
            })
            .Build();

        var admitted = new TaskCompletionSource<QueueAdmittedPush>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<QueueAdmittedPush>("queueAdmitted", push => admitted.TrySetResult(push));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinQueue", eventId, visitorId);

        await JoinQueueAsync(eventId, visitorId);

        var completed = await Task.WhenAny(admitted.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(completed == admitted.Task, "no queueAdmitted push within 20s - admitter/broadcaster chain broken");
        Assert.Equal(eventId, (await admitted.Task).EventId);
    }

    private sealed record QueueStatusDto(bool Admitted, long Position, long Waiting);
    private sealed record QueueAdmittedPush(Guid EventId);
}
