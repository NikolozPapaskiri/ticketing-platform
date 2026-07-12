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

        local quota = tonumber(ARGV[9])
        local admitted = {}
        if allowed > 0 then
          local popped = redis.call('ZPOPMIN', KEYS[1], allowed)
          local i = 1
          while i <= #popped do
            local member = popped[i]
            local akey = ARGV[2] .. member
            -- The grant is a hash: a hold quota (spent as it is used) and, once bound, the
            -- customer it belongs to. TTL keeps it short-lived.
            redis.call('HSET', akey, 'quota', quota)
            redis.call('EXPIRE', akey, ttl)
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

    /// <summary>
    /// Authorize a hold against a grant, atomically: 0=admitted (quota spent), 1=not admitted,
    /// 2=wrong customer, 3=quota exhausted. Binds the grant to the customer on first use so a
    /// leaked visitor id is useless to a different account. KEYS = admitKey; ARGV = customerKey.
    /// </summary>
    private const string ConsumeScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return 1 end
        local bound = redis.call('HGET', KEYS[1], 'customer')
        if bound == false or bound == '' then
          redis.call('HSET', KEYS[1], 'customer', ARGV[1])
        elseif bound ~= ARGV[1] then
          return 2
        end
        local remaining = tonumber(redis.call('HGET', KEYS[1], 'quota') or '0')
        if remaining <= 0 then return 3 end
        redis.call('HINCRBY', KEYS[1], 'quota', -1)
        return 0
        """;

    /// <summary>
    /// Fixed-window join throttle: 1=allowed, 0=throttled. KEYS = counter; ARGV = limit, window.
    /// </summary>
    private const string JoinThrottleScript = """
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then redis.call('EXPIRE', KEYS[1], tonumber(ARGV[2])) end
        if n > tonumber(ARGV[1]) then return 0 end
        return 1
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
                TokenKeyTtlSeconds,
                _options.AdmissionHoldQuota
            });

        var parts = (RedisResult[])result!;
        var admitted = ((RedisValue[])parts[0]!).Select(v => Guid.Parse((string)v!)).ToList();
        var stillWaiting = ((RedisValue[])parts[1]!).Select(v => Guid.Parse((string)v!)).ToList();
        return (admitted, stillWaiting);
    }

    public async Task<AdmissionOutcome> TryConsumeAdmissionAsync(
        Guid eventId, Guid visitorId, string customerKey, CancellationToken ct)
    {
        var result = await _redis.GetDatabase().ScriptEvaluateAsync(ConsumeScript,
            new RedisKey[] { AdmitKey(eventId, visitorId) },
            new RedisValue[] { customerKey });

        return (int)result switch
        {
            0 => AdmissionOutcome.Admitted,
            2 => AdmissionOutcome.WrongCustomer,
            3 => AdmissionOutcome.QuotaExhausted,
            _ => AdmissionOutcome.NotAdmitted
        };
    }

    public async Task<bool> TryRegisterJoinAsync(string clientKey, CancellationToken ct)
    {
        var result = await _redis.GetDatabase().ScriptEvaluateAsync(JoinThrottleScript,
            new RedisKey[] { $"wq:join_rl:{clientKey}" },
            new RedisValue[] { _options.JoinRateLimit, _options.JoinRateWindowSeconds });
        return (int)result == 1;
    }
}
