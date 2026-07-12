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

    /// <summary>Non-consuming check for the status view (does this visitor hold a live grant?).</summary>
    Task<bool> IsAdmittedAsync(Guid eventId, Guid visitorId, CancellationToken ct);

    /// <summary>
    /// The hold path's enforcement, done atomically in Redis: verifies the grant exists for THIS
    /// event, binds it to the customer on first use (a leaked visitor id is useless to another
    /// account), and decrements the per-admission hold quota - all in one round trip, so there is
    /// no check-then-act gap for a leaked or shared grant to slip through.
    /// </summary>
    Task<AdmissionOutcome> TryConsumeAdmissionAsync(Guid eventId, Guid visitorId, string customerKey, CancellationToken ct);

    /// <summary>Throttle anonymous queue joins per client so nobody mints unlimited positions.</summary>
    Task<bool> TryRegisterJoinAsync(string clientKey, CancellationToken ct);

    /// <summary>Total visitors queued across every active event - the ops snapshot's queue-depth gauge.</summary>
    Task<long> GetTotalWaitingAsync(CancellationToken ct);
}

/// <summary>Position is 1-based; 0 when admitted (no longer in line).</summary>
public sealed record WaitingRoomStatus(bool Admitted, long Position, long Waiting);

/// <summary>Result of authorizing a hold against a waiting-room admission grant.</summary>
public enum AdmissionOutcome
{
    Admitted,       // valid grant for this event + customer; quota decremented
    NotAdmitted,    // no live grant (never admitted, or it expired)
    WrongCustomer,  // the grant is bound to a different authenticated customer
    QuotaExhausted  // the grant's hold allowance is used up
}
