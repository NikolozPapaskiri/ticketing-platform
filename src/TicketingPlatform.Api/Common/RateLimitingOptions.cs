namespace TicketingPlatform.Api.Common;

/// <summary>
/// Auth brute-force limits (section <c>RateLimiting</c>). Two layers on purpose:
/// <see cref="AuthRequestsPerMinute"/> is the per-process ASP.NET Core window - cheap, in-memory,
/// and still standing if Redis is down; <see cref="GlobalAuthRequestsPerMinute"/> is the shared
/// cross-replica budget enforced in Redis, which is the one that does NOT multiply as replicas are
/// added. Keep the per-process value >= the global one so the global budget is the binding policy.
/// </summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Per-replica in-process permits per window.</summary>
    public int AuthRequestsPerMinute { get; init; } = 20;

    /// <summary>Permits per window shared by every replica (Redis-backed).</summary>
    public int GlobalAuthRequestsPerMinute { get; init; } = 20;

    /// <summary>Window length for both layers. Configurable so tests can use a short window.</summary>
    public int AuthWindowSeconds { get; init; } = 60;
}
