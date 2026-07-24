using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TicketingPlatform.Infrastructure.Security;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// In-process window raised out of the way, shared Redis budget tiny: whatever rejects the flood
/// here can only be the DISTRIBUTED limiter, which is the point of the test.
/// </summary>
public sealed class DistributedRateLimitApiFactory : TicketingApiFactory
{
    public const int GlobalLimit = 3;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:AuthRequestsPerMinute", "100000");             // per-replica: effectively off
        builder.UseSetting("RateLimiting:GlobalAuthRequestsPerMinute", GlobalLimit.ToString());
        builder.UseSetting("RateLimiting:AuthWindowSeconds", "60");
    }
}

/// <summary>
/// The cross-replica auth brute-force budget. A per-process limiter multiplies with replica count;
/// these tests prove the Redis-backed budget does not - two independent limiter instances (standing
/// in for two API replicas) draw from ONE budget - and that the auth endpoints enforce it.
/// </summary>
[Collection(nameof(ApiCollection))]
public class DistributedRateLimiterTests
{
    private readonly TicketingApiFactory _factory;
    public DistributedRateLimiterTests(TicketingApiFactory factory) => _factory = factory;

    private RedisFixedWindowRateLimiter NewLimiter() =>
        new(_factory.Services.GetRequiredService<IConnectionMultiplexer>(),
            NullLogger<RedisFixedWindowRateLimiter>.Instance);

    [Fact]
    public async Task TwoReplicas_ShareOneBudget()
    {
        var key = $"rl:test:{Guid.NewGuid():N}";
        var window = TimeSpan.FromMinutes(1);

        // "Replica A" and "replica B" are separate limiter instances over the same Redis.
        var replicaA = NewLimiter();
        var replicaB = NewLimiter();

        // A budget of 3 is spent across BOTH replicas, alternating...
        Assert.True(await replicaA.TryAcquireAsync(key, 3, window, CancellationToken.None));
        Assert.True(await replicaB.TryAcquireAsync(key, 3, window, CancellationToken.None));
        Assert.True(await replicaA.TryAcquireAsync(key, 3, window, CancellationToken.None));

        // ...so the 4th is rejected on EITHER replica. A per-process limiter would have allowed it,
        // because each replica would still have permits left in its own counter.
        Assert.False(await replicaB.TryAcquireAsync(key, 3, window, CancellationToken.None));
        Assert.False(await replicaA.TryAcquireAsync(key, 3, window, CancellationToken.None));
    }

    [Fact]
    public async Task SeparateClients_HaveSeparateBudgets()
    {
        var limiter = NewLimiter();
        var window = TimeSpan.FromMinutes(1);
        var clientOne = $"rl:test:{Guid.NewGuid():N}";
        var clientTwo = $"rl:test:{Guid.NewGuid():N}";

        Assert.True(await limiter.TryAcquireAsync(clientOne, 1, window, CancellationToken.None));
        Assert.False(await limiter.TryAcquireAsync(clientOne, 1, window, CancellationToken.None));

        // One client exhausting its budget must not throttle everyone else.
        Assert.True(await limiter.TryAcquireAsync(clientTwo, 1, window, CancellationToken.None));
    }

    [Fact]
    public async Task Window_ExpiresSoTheBudgetRefills()
    {
        var limiter = NewLimiter();
        var key = $"rl:test:{Guid.NewGuid():N}";

        Assert.True(await limiter.TryAcquireAsync(key, 1, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(await limiter.TryAcquireAsync(key, 1, TimeSpan.FromSeconds(1), CancellationToken.None));

        // The counter key carries the window's TTL, so the budget comes back on its own.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.True(await limiter.TryAcquireAsync(key, 1, TimeSpan.FromSeconds(1), CancellationToken.None));
    }
}

/// <summary>The auth endpoints enforce the shared budget (own containers: the shared factory runs with it raised).</summary>
public class DistributedAuthEndpointRateLimitTests : IClassFixture<DistributedRateLimitApiFactory>
{
    private readonly HttpClient _client;
    public DistributedAuthEndpointRateLimitTests(DistributedRateLimitApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task LoginFlood_IsCutOffByTheSharedBudget()
    {
        var body = new { email = $"attacker-{Guid.NewGuid():N}@example.com", password = "guess" };

        // The in-process window is effectively disabled here, so these 401s and the following 429
        // are decided by the Redis budget alone.
        for (var i = 0; i < DistributedRateLimitApiFactory.GlobalLimit; i++)
        {
            var allowed = await _client.PostAsJsonAsync("/api/v1/auth/login", body);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var blocked = await _client.PostAsJsonAsync("/api/v1/auth/login", body);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }
}
