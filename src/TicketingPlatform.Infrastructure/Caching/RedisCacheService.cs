using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Infrastructure.Caching;

/// <summary>
/// Redis-backed ICacheService over IDistributedCache with System.Text.Json payloads.
/// TTLs get +-20% jitter: if a hot key is written for many entities at the same moment,
/// identical TTLs make them all expire together and the herd of misses lands on the database
/// at once (a mini stampede). Jitter spreads the expiry.
/// Failure posture: a cache outage must never take the API down - reads fall through to the
/// database, writes are best-effort. The cache is an optimization, not a dependency.
/// </summary>
public sealed class RedisCacheService : ICacheService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;

    public RedisCacheService(IDistributedCache cache) => _cache = cache;

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct)
    {
        try
        {
            var bytes = await _cache.GetAsync(key, ct);
            return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch
        {
            return default; // cache down => miss, the caller hits the DB
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var jittered = ttl + TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)(ttl.TotalMilliseconds * 0.2)));
            await _cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value, Json),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = jittered }, ct);
        }
        catch
        {
            // best-effort: losing a cache write costs one extra DB read later, nothing more
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        try { await _cache.RemoveAsync(key, ct); }
        catch { /* invalidation failure degrades to TTL expiry */ }
    }
}
