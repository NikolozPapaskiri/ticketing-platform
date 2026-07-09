using TicketingPlatform.Application.Common;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// THE marquee port of the project: how inventory is decremented safely when many buyers hit
/// the same row at once. Three interchangeable implementations live in Infrastructure -
/// selected by the "Reservation:Strategy" config value (see appsettings.json):
///
///   "OptimisticConcurrency" (DEFAULT) - relies on the Postgres xmin row-version token: writers
///       do not block each other; a conflicting commit throws DbUpdateConcurrencyException and
///       the loser retries against fresh values. Best when conflicts are the exception.
///       Degrades under extreme single-row contention (retry storms).
///
///   "PessimisticLock" - SELECT ... FOR UPDATE: the row is locked, competing buyers QUEUE.
///       Always correct, zero wasted work, but the hot row serializes all throughput and locks
///       held across slow work invite timeouts. Best when conflicts are the norm (a flash sale).
///
///   "RedisAtomic" - the counter moves to Redis; DECRBY is atomic, so the database is out of
///       the hot path entirely. Highest throughput, but now two stores must agree - the demo
///       implementation documents the drift window and its production fixes.
///
/// The contract: ReserveAsync persists the inventory decrement AND the hold row atomically
/// (or returns Conflict, changing nothing). ReleaseAsync persists the give-back.
/// </summary>
public interface IReservationStrategy
{
    Task<Result> ReserveAsync(Hold hold, CancellationToken ct);
    Task ReleaseAsync(Hold hold, CancellationToken ct);
}
