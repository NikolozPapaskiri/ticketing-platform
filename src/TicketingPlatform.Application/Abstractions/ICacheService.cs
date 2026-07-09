namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for the distributed cache (Redis in Infrastructure). Distributed - not IMemoryCache -
/// because the moment there are two API replicas, in-process caches diverge and invalidation
/// on one pod does nothing for the other.
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct);
    Task RemoveAsync(string key, CancellationToken ct);
}
