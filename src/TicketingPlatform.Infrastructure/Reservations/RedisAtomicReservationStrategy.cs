using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Infrastructure.Reservations;

/// <summary>
/// Redis atomic decrement: the availability counter lives in Redis, and DECRBY is atomic, so
/// thousands of concurrent buyers never touch the database on the hot path - the DB write
/// happens only for winners. Highest throughput of the three strategies; this is what an
/// on-sale spike wants.
/// The price is TWO stores that must agree. This demo implementation has a documented drift
/// window: a crash between the Redis DECRBY and the DB commit strands reserved counter units
/// until the key expires. Production hardening options (Phase 6+ material): a Lua script that
/// couples check+decr+audit, an async reconciliation job comparing Redis to the DB, or
/// treating Redis as the sole authority with write-behind persistence.
/// </summary>
public sealed class RedisAtomicReservationStrategy : IReservationStrategy
{
    /// <summary>Counter keys expire so a drifted counter self-heals from the DB on next use.</summary>
    private static readonly TimeSpan CounterTtl = TimeSpan.FromMinutes(30);

    private readonly TicketingDbContext _db;
    private readonly IConnectionMultiplexer _redis;

    public RedisAtomicReservationStrategy(TicketingDbContext db, IConnectionMultiplexer redis)
    {
        _db = db;
        _redis = redis;
    }

    private static string CounterKey(Guid tenantId, Guid ticketTypeId) => $"inv:{tenantId}:{ticketTypeId}";

    public async Task<Result> ReserveAsync(Hold hold, CancellationToken ct)
    {
        var inventory = await _db.Inventories.FirstOrDefaultAsync(i => i.TicketTypeId == hold.TicketTypeId, ct);
        if (inventory is null)
            return Result.NotFound($"Ticket type '{hold.TicketTypeId}' was not found.");

        var redis = _redis.GetDatabase();
        var key = CounterKey(hold.TenantId, hold.TicketTypeId);

        // Lazy seed from the source of truth. NX = only when absent, so two racers cannot
        // both seed (the second SET NX is a no-op).
        await redis.StringSetAsync(key, inventory.AvailableQuantity, CounterTtl, When.NotExists);

        // THE atomic gate: one round trip, no read-check-write race possible.
        var remaining = await redis.StringDecrementAsync(key, hold.Quantity);
        if (remaining < 0)
        {
            // Overshot: give the units back atomically and reject.
            await redis.StringIncrementAsync(key, hold.Quantity);
            return Result.Conflict(
                $"Cannot hold {hold.Quantity} tickets; only {Math.Max(0, remaining + hold.Quantity)} available.");
        }

        try
        {
            // Winner: persist to the source of truth (drift window starts here - see class docs).
            inventory.AvailableQuantity -= hold.Quantity;
            _db.Holds.Add(hold);
            await _db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch
        {
            await redis.StringIncrementAsync(key, hold.Quantity); // compensate the counter
            throw;
        }
    }

    public async Task ReleaseAsync(Hold hold, CancellationToken ct)
    {
        hold.TicketType.Inventory.Release(hold.Quantity);
        await _db.SaveChangesAsync(ct);

        // Credit the counter only if it exists (expired counter re-seeds from the DB anyway).
        var redis = _redis.GetDatabase();
        var key = CounterKey(hold.TenantId, hold.TicketTypeId);
        if (await redis.KeyExistsAsync(key))
            await redis.StringIncrementAsync(key, hold.Quantity);
    }
}
