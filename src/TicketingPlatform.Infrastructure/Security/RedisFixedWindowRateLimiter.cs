using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Infrastructure.Security;

/// <summary>
/// Redis fixed-window counter: INCR the window key, set its TTL on first use, and reject once the
/// count passes the permit limit - all inside one Lua script, so requests racing across replicas
/// cannot interleave their way into extra permits. Same shape as the waiting room's join throttle.
/// The key expires with the window, so there is nothing to clean up.
/// </summary>
public sealed class RedisFixedWindowRateLimiter : IDistributedRateLimiter
{
    // KEYS[1] = window key; ARGV[1] = permit limit; ARGV[2] = window seconds.
    private const string Script = """
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then redis.call('EXPIRE', KEYS[1], tonumber(ARGV[2])) end
        if n > tonumber(ARGV[1]) then return 0 end
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisFixedWindowRateLimiter> _logger;

    public RedisFixedWindowRateLimiter(IConnectionMultiplexer redis, ILogger<RedisFixedWindowRateLimiter> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> TryAcquireAsync(string key, int permitLimit, TimeSpan window, CancellationToken ct)
    {
        var windowSeconds = Math.Max(1, (int)window.TotalSeconds);

        try
        {
            var result = await _redis.GetDatabase().ScriptEvaluateAsync(Script,
                new RedisKey[] { key },
                new RedisValue[] { permitLimit, windowSeconds });
            return (int)result == 1;
        }
        // Deliberately broad: a limiter fault must never be the reason nobody can sign in. Failing
        // open is safe because the per-process limiter still sits in front of the password hasher.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Distributed rate limiter unavailable; allowing the request");
            return true;
        }
    }
}
