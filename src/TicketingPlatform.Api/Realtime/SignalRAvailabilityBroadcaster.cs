using Microsoft.AspNetCore.SignalR;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Api.Realtime;

/// <summary>
/// The Api-layer implementation of the broadcast port: the Infrastructure projection consumer
/// calls IAvailabilityBroadcaster; only this class knows that means SignalR.
/// </summary>
public sealed class SignalRAvailabilityBroadcaster : IAvailabilityBroadcaster
{
    private readonly IHubContext<AvailabilityHub> _hub;
    public SignalRAvailabilityBroadcaster(IHubContext<AvailabilityHub> hub) => _hub = hub;

    public Task BroadcastAsync(Guid eventId, Guid ticketTypeId, int available, CancellationToken ct) =>
        _hub.Clients.Group(AvailabilityHub.GroupName(eventId))
            .SendAsync("availabilityChanged", new { ticketTypeId, available }, ct);
}
