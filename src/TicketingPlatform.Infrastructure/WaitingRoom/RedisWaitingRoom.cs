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
    private static string AdmitPrefix(Guid eventId) => $"wq:admit:{eventId}:";
    private static string TokensKey(Guid eventId) => $"wq:tokens:{eventId}";
    private static string TokensTsKey(Guid eventId) => $"wq:tokens_ts:{eventId}";

    private const int PositionsLimit = 49; // cap position pushes at the visible head of the line
    private const int TokenKeyTtlSeconds = 3600; // self-clean bucket keys for long-idle events

    /// <summary>
    /// The admission valve as ONE atomic Redis operation - no crash window can leave a visitor
    /// popped from the line without a TTL'd grant. A shared token bucket (refilled from Redis's
    /// own clock) caps the GLOBAL rate, so running the admitter on N replicas cannot exceed it.
    /// The event is de-registered from the active set only when the line is empty AT SCRIPT TIME,
    /// so a concurrent join can never be orphaned by a racing cleanup.
    /// KEYS = line, active, tokens, tokensTs;
    /// ARGV = eventId, admitPrefix, ratePerSec, capacity, batchMax, admitTtlSec, positionsLimit, tokenKeyTtl.
    /// </summary>
    private const string AdmitScript = """
        local t = redis.call('TIME')
        local now = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
        local rate = tonumber(ARGV[3])
        local capacity = tonumber(ARGV[4])
        local batchMax = tonumber(ARGV[5])
        local ttl = tonumber(ARGV[6])
        local posLimit = tonumber(ARGV[7])
        local keyTtl = tonumber(ARGV[8])

        local tokens = tonumber(redis.call('GET', KEYS[3]))
        if tokens == nil then tokens = capacity end
        local ts = tonumber(redis.call('GET', KEYS[4]))
        if ts == nil then ts = now end
        local elapsed = now - ts
        if elapsed < 0 then elapsed = 0 end
        tokens = math.min(capacity, tokens + (elapsed / 1000.0) * rate)

        local queueLen = redis.call('ZCARD', KEYS[1])
        local allowed = math.floor(math.min(tokens, batchMax, queueLen))
        if allowed < 0 then allowed = 0 end

        local admitted = {}
        if allowed > 0 then
          local popped = redis.call('ZPOPMIN', KEYS[1], allowed)
          local i = 1
          while i <= #popped do
            local member = popped[i]
            redis.call('SET', ARGV[2] .. member, '1', 'EX', ttl)
            admitted[#admitted + 1] = member
            i = i + 2
          end
          tokens = tokens - #admitted
        end

        redis.call('SET', KEYS[3], tokens, 'EX', keyTtl)
        redis.call('SET', KEYS[4], now, 'EX', keyTtl)

        local waiting = redis.call('ZRANGE', KEYS[1], 0, posLimit)

        if redis.call('ZCARD', KEYS[1]) == 0 then
          redis.call('SREM', KEYS[2], ARGV[1])
        end

        return { admitted, waiting }
        """;

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
    /// One admission tick, executed as a single atomic Lua script (see <see cref="AdmitScript"/>):
    /// the global token bucket decides how many may pass, they are popped from the line AND granted
    /// TTL'd admissions in the same operation, positions are read, and the event is de-registered
    /// only if the line is empty. Returns who got in plus who is still waiting (for position pushes).
    /// </summary>
    public async Task<(IReadOnlyList<Guid> Admitted, IReadOnlyList<Guid> StillWaiting)> AdmitBatchAsync(
        Guid eventId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();

        var result = await db.ScriptEvaluateAsync(AdmitScript,
            new RedisKey[] { LineKey(eventId), ActiveQueuesKey, TokensKey(eventId), TokensTsKey(eventId) },
            new RedisValue[]
            {
                eventId.ToString(),
                AdmitPrefix(eventId),
                _options.AdmitRatePerSecond,
                _options.AdmitBurst,
                _options.AdmitBatchSize,
                _options.AdmissionTtlSeconds,
                PositionsLimit,
                TokenKeyTtlSeconds
            });

        var parts = (RedisResult[])result!;
        var admitted = ((RedisValue[])parts[0]!).Select(v => Guid.Parse((string)v!)).ToList();
        var stillWaiting = ((RedisValue[])parts[1]!).Select(v => Guid.Parse((string)v!)).ToList();
        return (admitted, stillWaiting);
    }
}
