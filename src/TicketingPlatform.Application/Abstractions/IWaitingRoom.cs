namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for the virtual waiting room - queue-based load leveling for hot on-sales. The line
/// lives in Redis (a sorted set scored by arrival time), NOT in process memory: a visitor's
/// position survives page refreshes, API restarts, and works identically across N replicas.
/// A background admitter moves the head of the line into a TTL'd "admitted" set at a
/// controlled rate; only admitted visitors may reserve inventory. This is backpressure: the
/// spike waits in Redis instead of stampeding the database.
/// </summary>
public interface IWaitingRoom
{
    /// <summary>Idempotent: joining twice keeps the original position (ZADD NX).</summary>
    Task<WaitingRoomStatus> JoinAsync(Guid eventId, Guid visitorId, CancellationToken ct);

    Task<WaitingRoomStatus> GetStatusAsync(Guid eventId, Guid visitorId, CancellationToken ct);

    /// <summary>The enforcement check the hold path calls before reserving.</summary>
    Task<bool> IsAdmittedAsync(Guid eventId, Guid visitorId, CancellationToken ct);
}

/// <summary>Position is 1-based; 0 when admitted (no longer in line).</summary>
public sealed record WaitingRoomStatus(bool Admitted, long Position, long Waiting);
