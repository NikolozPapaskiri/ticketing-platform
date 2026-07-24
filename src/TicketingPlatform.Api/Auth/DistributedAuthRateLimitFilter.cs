using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TicketingPlatform.Api.Common;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;

namespace TicketingPlatform.Api.Auth;

/// <summary>
/// The cross-replica half of the auth brute-force guard. The <c>UseRateLimiter</c> policy counts in
/// process memory, so with N replicas an attacker's effective budget is N x the limit - scaling out
/// weakens the guard. This filter counts the same client's attempts in Redis, so the budget is
/// global and replica-count independent. It runs before the action, so a rejected attempt never
/// reaches the password hasher. Both layers stay: if Redis is unreachable the limiter fails open and
/// the in-process window is the backstop.
/// </summary>
public sealed class DistributedAuthRateLimitFilter : IAsyncActionFilter
{
    private readonly IDistributedRateLimiter _limiter;
    private readonly RateLimitingOptions _options;

    public DistributedAuthRateLimitFilter(IDistributedRateLimiter limiter, RateLimitingOptions options)
    {
        _limiter = limiter;
        _options = options;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Same partition key as the in-process policy. When a trusted proxy is configured, forwarded
        // headers have already rewritten RemoteIpAddress by the time MVC filters run.
        var clientKey = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var allowed = await _limiter.TryAcquireAsync(
            $"rl:auth:{clientKey}",
            _options.GlobalAuthRequestsPerMinute,
            TimeSpan.FromSeconds(_options.AuthWindowSeconds),
            context.HttpContext.RequestAborted);

        if (!allowed)
        {
            TicketingMetrics.AuthRateLimited.Add(1);
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = "Too many authentication attempts. Try again shortly."
            })
            {
                StatusCode = StatusCodes.Status429TooManyRequests
            };
            return;
        }

        await next();
    }
}
