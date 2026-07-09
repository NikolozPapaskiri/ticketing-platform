namespace TicketingPlatform.Application.Common;

/// <summary>
/// One place for cache key shapes so producers and invalidators can never drift apart.
/// Keys are ALWAYS tenant-prefixed: the cache is shared across requests, and a key without the
/// tenant would let one tenant read another's cached data, silently bypassing the EF filters.
/// </summary>
public static class CacheKeys
{
    public static string EventGraph(Guid tenantId, Guid eventId) => $"tenant:{tenantId}:event:{eventId}";
}
