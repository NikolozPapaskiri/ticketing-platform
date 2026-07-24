using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using TicketingPlatform.Api.Auth;
using TicketingPlatform.Api.Common;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.UnitTests.Auth;

/// <summary>
/// The cross-replica auth guard: it must short-circuit with 429 BEFORE the action runs (so a
/// rejected attempt never reaches the password hasher), partition by client IP, and otherwise get
/// out of the way.
/// </summary>
public class DistributedAuthRateLimitFilterTests
{
    private sealed class StubLimiter : IDistributedRateLimiter
    {
        private readonly bool _allow;
        public StubLimiter(bool allow) => _allow = allow;

        public string? LastKey { get; private set; }
        public int? LastPermitLimit { get; private set; }
        public TimeSpan? LastWindow { get; private set; }

        public Task<bool> TryAcquireAsync(string key, int permitLimit, TimeSpan window, CancellationToken ct)
        {
            LastKey = key;
            LastPermitLimit = permitLimit;
            LastWindow = window;
            return Task.FromResult(_allow);
        }
    }

    private static ActionExecutingContext Context(string clientIp)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());
    }

    [Fact]
    public async Task OverBudget_ShortCircuitsWith429_AndNeverRunsTheAction()
    {
        var limiter = new StubLimiter(allow: false);
        var filter = new DistributedAuthRateLimitFilter(limiter, new RateLimitingOptions());
        var context = Context("203.0.113.7");
        var actionRan = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.False(actionRan); // the password hasher is never reached
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Too many requests", problem.Title);
    }

    [Fact]
    public async Task WithinBudget_RunsTheActionAndDoesNotSetAResult()
    {
        var filter = new DistributedAuthRateLimitFilter(new StubLimiter(allow: true), new RateLimitingOptions());
        var context = Context("203.0.113.8");
        var actionRan = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.True(actionRan);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task PartitionsByClientIp_UsingTheConfiguredGlobalBudget()
    {
        var limiter = new StubLimiter(allow: true);
        var options = new RateLimitingOptions { GlobalAuthRequestsPerMinute = 7, AuthWindowSeconds = 30 };
        var filter = new DistributedAuthRateLimitFilter(limiter, options);

        await filter.OnActionExecutionAsync(Context("198.51.100.4"),
            () => Task.FromResult<ActionExecutedContext>(null!));

        Assert.Contains("198.51.100.4", limiter.LastKey);
        Assert.Equal(7, limiter.LastPermitLimit);                       // the GLOBAL budget, not the per-replica one
        Assert.Equal(TimeSpan.FromSeconds(30), limiter.LastWindow);
    }
}
