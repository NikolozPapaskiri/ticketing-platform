using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// DataProtection with the Redis provider shares ONE key ring across instances - the multi-replica
/// property a per-pod filesystem ring lacks. Proven by protecting with one provider instance and
/// unprotecting with a second, independent instance pointed at the same Redis + application name.
/// </summary>
[Collection(nameof(ApiCollection))]
public class DataProtectionKeyRingTests
{
    private readonly TicketingApiFactory _factory;
    public DataProtectionKeyRingTests(TicketingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RedisKeyRing_IsSharedAcrossInstances()
    {
        var redis = _factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var ringKey = "ticketing:dataprotection:keys:test-" + Guid.NewGuid().ToString("N");

        // "Pod A" protects a payload...
        var instanceA = BuildProvider(redis, ringKey);
        var protectedPayload = instanceA.CreateProtector("ticket-download").Protect("order-42");

        // ...and a separate "Pod B" (same Redis ring + application name) can unprotect it.
        var instanceB = BuildProvider(redis, ringKey);
        var recovered = instanceB.CreateProtector("ticket-download").Unprotect(protectedPayload);
        Assert.Equal("order-42", recovered);

        // The key ring lives in Redis (shared), not on a per-pod disk.
        Assert.True(await redis.GetDatabase().KeyExistsAsync(ringKey));
    }

    private static IDataProtectionProvider BuildProvider(IConnectionMultiplexer redis, string ringKey)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToStackExchangeRedis(redis, ringKey)
            .SetApplicationName("ticketing-platform");
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }
}
