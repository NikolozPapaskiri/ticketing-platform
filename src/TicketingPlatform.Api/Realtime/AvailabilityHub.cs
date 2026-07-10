using Microsoft.AspNetCore.SignalR;

namespace TicketingPlatform.Api.Realtime;

/// <summary>
/// Live availability push. Clients join a per-event group and receive "availabilityChanged"
/// messages whenever the projection updates. Anonymous by design: watching ticket counts is
/// the public, buyer-facing part of the domain.
/// Multi-replica note: group membership lives in the SignalR backplane (Redis, wired in
/// Program) - without it, a client connected to pod A never hears a broadcast sent from pod B.
/// That is the in-process-state-breaks-at-two-replicas lesson, same as caching and locks.
/// </summary>
public sealed class AvailabilityHub : Hub
{
    public Task JoinEvent(Guid eventId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupName(eventId));

    public Task LeaveEvent(Guid eventId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(eventId));

    /// <summary>
    /// Waiting-room subscription: a per-visitor group so admission ("queueAdmitted") and live
    /// position updates ("queuePosition") reach exactly one browser. The visitorId is a
    /// client-generated GUID - unguessable enough that another visitor can't listen in, and
    /// admission itself is still verified server-side on the hold endpoint.
    /// </summary>
    public Task JoinQueue(Guid eventId, Guid visitorId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, QueueGroupName(eventId, visitorId));

    public Task LeaveQueue(Guid eventId, Guid visitorId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, QueueGroupName(eventId, visitorId));

    internal static string GroupName(Guid eventId) => $"event:{eventId}";
    internal static string QueueGroupName(Guid eventId, Guid visitorId) => $"queue:{eventId}:{visitorId}";
}
