namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for pushing waiting-room progress to a specific visitor. Implemented in the Api layer
/// with SignalR (per-visitor groups), same inward-dependency pattern as IAvailabilityBroadcaster.
/// </summary>
public interface IQueueBroadcaster
{
    Task NotifyAdmittedAsync(Guid eventId, Guid visitorId, CancellationToken ct);
    Task NotifyPositionAsync(Guid eventId, Guid visitorId, long position, CancellationToken ct);
}
