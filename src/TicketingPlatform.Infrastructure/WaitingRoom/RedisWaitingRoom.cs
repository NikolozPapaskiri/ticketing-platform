using StackExchange.Redis;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;

namespace TicketingPlatform.Infrastructure.WaitingRoom;

/// <summary>
/// Redis implementation of the waiting room.
///  - The LINE is a sorted set scored by arrival ms (FIFO by construction; ZADD NX makes
///    joining idempotent - refreshing the page never loses your place).
///  - ADMISSION is a per-visitor key with a TTL (time to shop), written by the admitter.
///  - A registry set tracks which events currently have a line, so the admitter never scans.
/// All state is in Redis on purpose: positions survive restarts and are identical across
/// replicas - the same "no correctness in process memory" rule as the cache and the locks.
/// </summary>
public sealed class RedisWaitingRoom : IWaitingRoom
{
    private const string ActiveQueuesKey = "wq:active";
    private static string LineKey(Guid eventId) => $"wq:line:{eventId}";
    private static string AdmitKey(Guid eventId, Guid visitorId) => $"wq:admit:{eventId}:{visitorId}";

    private readonly IConnectionMultiplexer _redis;
    private readonly WaitingRoomOptions _options;
    private readonly TimeProvider _clock;

    public RedisWaitingRoom(IConnectionMultiplexer redis, WaitingRoomOptions options, TimeProvider clock)
    {
        _redis = redis;
        _options = options;
        _clock = clock;
    }

    public async Task<WaitingRoomStatus> JoinAsync(Guid eventId, Guid visitorId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        if (await db.KeyExistsAsync(AdmitKey(eventId, visitorId)))
            return new WaitingRoomStatus(true, 0, await db.SortedSetLengthAsync(LineKey(eventId)));

        // NX: a rejoin keeps the ORIGINAL arrival score - no queue-jumping by re-entering.
        await db.SortedSetAddAsync(LineKey(eventId), visitorId.ToString(),
            _clock.GetUtcNow().ToUnixTimeMilliseconds(), When.NotExists);
        await db.SetAddAsync(ActiveQueuesKey, eventId.ToString());

        return await GetStatusAsync(eventId, visitorId, ct);
    }

    public async Task<WaitingRoomStatus> GetStatusAsync(Guid eventId, Guid visitorId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        if (await db.KeyExistsAsync(AdmitKey(eventId, visitorId)))
            return new WaitingRoomStatus(true, 0, await db.SortedSetLengthAsync(LineKey(eventId)));

        var rank = await db.SortedSetRankAsync(LineKey(eventId), visitorId.ToString());
        var waiting = await db.SortedSetLengthAsync(LineKey(eventId));
        return new WaitingRoomStatus(false, rank is null ? 0 : rank.Value + 1, waiting);
    }

    public async Task<bool> IsAdmittedAsync(Guid eventId, Guid visitorId, CancellationToken ct) =>
        await _redis.GetDatabase().KeyExistsAsync(AdmitKey(eventId, visitorId));

    /// <summary>Events that currently have somebody in line (the admitter's work list).</summary>
    public async Task<IReadOnlyList<Guid>> GetActiveQueuesAsync(CancellationToken ct)
    {
        var members = await _redis.GetDatabase().SetMembersAsync(ActiveQueuesKey);
        // Explicit string cast: RedisValue converts to string AND byte[], and .NET 10 added a
        // UTF-8 Guid.Parse(ReadOnlySpan<byte>) overload, making the bare call ambiguous.
        return members.Select(m => Guid.Parse((string)m!)).ToList();
    }

    /// <summary>
    /// One admission tick: pop the head of the line (ZPOPMIN is atomic - two admitter
    /// replicas cannot admit the same visitor twice), grant TTL'd admissions, and return
    /// who got in plus who is still waiting (for position pushes).
    /// </summary>
    public async Task<(IReadOnlyList<Guid> Admitted, IReadOnlyList<Guid> StillWaiting)> AdmitBatchAsync(
        Guid eventId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var popped = await db.SortedSetPopAsync(LineKey(eventId), _options.AdmitBatchSize);

        var admitted = new List<Guid>(popped.Length);
        foreach (var entry in popped)
        {
            var visitorId = Guid.Parse((string)entry.Element!);
            await db.StringSetAsync(AdmitKey(eventId, visitorId), "1", _options.AdmissionTtl);
            admitted.Add(visitorId);
        }

        // Cap position pushes at the visible head of the line; deep positions change slowly.
        var waitingValues = await db.SortedSetRangeByRankAsync(LineKey(eventId), 0, 49);
        var stillWaiting = waitingValues.Select(v => Guid.Parse((string)v!)).ToList();

        if (stillWaiting.Count == 0)
            await db.SetRemoveAsync(ActiveQueuesKey, eventId.ToString());

        return (admitted, stillWaiting);
    }
}
