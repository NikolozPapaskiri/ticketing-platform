using Microsoft.AspNetCore.SignalR;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Api.Realtime;

/// <summary>
/// Api-layer implementation of the waiting-room broadcast port: the Infrastructure admitter
/// calls IQueueBroadcaster; only this class knows that means SignalR per-visitor groups.
/// </summary>
public sealed class SignalRQueueBroadcaster : IQueueBroadcaster
{
    private readonly IHubContext<AvailabilityHub> _hub;
    public SignalRQueueBroadcaster(IHubContext<AvailabilityHub> hub) => _hub = hub;

    public Task NotifyAdmittedAsync(Guid eventId, Guid visitorId, CancellationToken ct) =>
        _hub.Clients.Group(AvailabilityHub.QueueGroupName(eventId, visitorId))
            .SendAsync("queueAdmitted", new { eventId }, ct);

    public Task NotifyPositionAsync(Guid eventId, Guid visitorId, long position, CancellationToken ct) =>
        _hub.Clients.Group(AvailabilityHub.QueueGroupName(eventId, visitorId))
            .SendAsync("queuePosition", new { eventId, position }, ct);
}
