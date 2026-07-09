namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for pushing live availability to connected clients. Implemented in the Api layer with
/// SignalR (the hub is a web concern) - Infrastructure's projection consumer calls this port
/// without knowing SignalR exists, which keeps the dependency arrows pointing inward.
/// </summary>
public interface IAvailabilityBroadcaster
{
    Task BroadcastAsync(Guid eventId, Guid ticketTypeId, int available, CancellationToken ct);
}
