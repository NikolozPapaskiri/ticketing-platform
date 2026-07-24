namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// A fixed-window rate limiter whose counters live OUTSIDE the process, so one budget is shared by
/// every replica. ASP.NET Core's built-in limiter is per-process: with N replicas an attacker gets
/// N x the configured budget, and adding capacity silently weakens the brute-force guard. This port
/// removes that multiplication.
/// </summary>
public interface IDistributedRateLimiter
{
    /// <summary>
    /// Counts one request against <paramref name="key"/>'s budget for the current window.
    /// <c>true</c> = within budget (proceed), <c>false</c> = over budget (reject).
    /// Implementations FAIL OPEN when the backing store is unreachable: a Redis outage must not lock
    /// every user out of signing in, and the per-process limiter still guards each replica.
    /// </summary>
    Task<bool> TryAcquireAsync(string key, int permitLimit, TimeSpan window, CancellationToken ct);
}
