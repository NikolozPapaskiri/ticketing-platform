using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;

namespace TicketingPlatform.Application.Services;

/// <summary>
/// Waiting-room use cases behind the anonymous queue endpoints. Owns the policy decisions:
/// an unknown or off-sale event is NotFound, and an event WITHOUT a waiting room admits
/// trivially (the client gate then renders nothing) - only the Redis line itself lives
/// behind the IWaitingRoom port.
/// </summary>
public sealed class WaitingRoomService
{
    private static readonly QueueStatusResponse TriviallyAdmitted = new(Admitted: true, Position: 0, Waiting: 0);

    private readonly IEventRepository _events;
    private readonly IWaitingRoom _waitingRoom;

    public WaitingRoomService(IEventRepository events, IWaitingRoom waitingRoom)
    {
        _events = events;
        _waitingRoom = waitingRoom;
    }

    public async Task<Result<QueueStatusResponse>> JoinAsync(
        Guid eventId, Guid visitorId, string clientKey, CancellationToken ct)
    {
        var state = await _events.GetWaitingRoomStateAsync(eventId, ct);
        if (state is null || !state.OnSale)
            return Result<QueueStatusResponse>.NotFound($"Event '{eventId}' is not on sale.");
        if (!state.WaitingRoomEnabled)
            return Result<QueueStatusResponse>.Success(TriviallyAdmitted);

        // Throttle joins per client so a script can't mint unlimited queue positions with fresh GUIDs.
        if (!await _waitingRoom.TryRegisterJoinAsync(clientKey, ct))
            return Result<QueueStatusResponse>.Throttled("Too many queue joins from this client; slow down.");

        var status = await _waitingRoom.JoinAsync(eventId, visitorId, ct);
        return Result<QueueStatusResponse>.Success(Map(status));
    }

    public async Task<Result<QueueStatusResponse>> GetStatusAsync(Guid eventId, Guid visitorId, CancellationToken ct)
    {
        var state = await _events.GetWaitingRoomStateAsync(eventId, ct);
        if (state is null || !state.OnSale)
            return Result<QueueStatusResponse>.NotFound($"Event '{eventId}' is not on sale.");
        if (!state.WaitingRoomEnabled)
            return Result<QueueStatusResponse>.Success(TriviallyAdmitted);

        var status = await _waitingRoom.GetStatusAsync(eventId, visitorId, ct);
        return Result<QueueStatusResponse>.Success(Map(status));
    }

    private static QueueStatusResponse Map(WaitingRoomStatus status) =>
        new(status.Admitted, status.Position, status.Waiting);
}
