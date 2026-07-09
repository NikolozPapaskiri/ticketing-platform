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

    internal static string GroupName(Guid eventId) => $"event:{eventId}";
}
