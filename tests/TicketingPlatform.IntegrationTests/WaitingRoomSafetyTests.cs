using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Infrastructure.WaitingRoom;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Test host with a low, well-known global admission rate and the background admitter effectively
/// disabled (a 1-hour interval), so the tests drive <see cref="RedisWaitingRoom.AdmitBatchAsync"/>
/// directly against real Redis without the hosted valve racing them.
/// </summary>
public sealed class WaitingRoomApiFactory : TicketingApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("WaitingRoom:AdmitRatePerSecond", "5");
        builder.UseSetting("WaitingRoom:AdmitBurst", "5");
        builder.UseSetting("WaitingRoom:AdmitBatchSize", "5");
        builder.UseSetting("WaitingRoom:AdmissionTtlSeconds", "300");
        builder.UseSetting("WaitingRoom:AdmitIntervalSeconds", "3600"); // background admitter out of the way
    }
}

/// <summary>
/// PR 4 - waiting-room atomicity and global admission control. Admission is one atomic Redis
/// script (no pop-before-grant crash window), the rate is a shared token bucket (replica-count
/// independent), and a join can never be orphaned by a racing active-registry cleanup.
/// </summary>
[Collection(nameof(WaitingRoomSafetyCollection))]
public sealed class WaitingRoomSafetyTests
{
    private readonly RedisWaitingRoom _room;

    public WaitingRoomSafetyTests(WaitingRoomApiFactory factory) =>
        _room = factory.Services.GetRequiredService<RedisWaitingRoom>();

    [Fact]
    public async Task Admission_ScriptCannotPopWithoutGrant()
    {
        var eventId = Guid.NewGuid();
        var visitors = NewVisitors(8);
        foreach (var v in visitors)
            await _room.JoinAsync(eventId, v, CancellationToken.None);

        var (admitted, _) = await _room.AdmitBatchAsync(eventId, CancellationToken.None);

        // Conservation: every visitor is EITHER granted (has an admit key) OR still in the line -
        // never popped without a grant. The atomic script forbids the popped-but-ungranted state.
        var granted = 0;
        var waiting = 0;
        foreach (var v in visitors)
        {
            var status = await _room.GetStatusAsync(eventId, v, CancellationToken.None);
            if (status.Admitted) granted++;
            else waiting++;
        }

        Assert.All(admitted, id => Assert.Contains(id, visitors));
        Assert.Equal(admitted.Count, granted);          // each admitted visitor really has a grant
        Assert.Equal(visitors.Count, granted + waiting); // no visitor vanished between pop and grant
    }

    [Fact]
    public async Task ConcurrentAdmitters_RespectOneGlobalRate()
    {
        var eventId = Guid.NewGuid();
        var visitors = NewVisitors(60);
        foreach (var v in visitors)
            await _room.JoinAsync(eventId, v, CancellationToken.None);

        // Two "replicas" hammer the valve for ~1.5s. With rate 5/s + burst 5 the GLOBAL bucket
        // must cap total admissions near 5*1.5 + 5 - far below 60. The old per-replica pop had no
        // shared limit, so both drained the whole line almost instantly.
        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
        async Task DrainAsync()
        {
            while (DateTime.UtcNow < deadline)
            {
                await _room.AdmitBatchAsync(eventId, CancellationToken.None);
                await Task.Delay(20);
            }
        }
        await Task.WhenAll(DrainAsync(), DrainAsync());

        var admittedCount = 0;
        foreach (var v in visitors)
            if (await _room.IsAdmittedAsync(eventId, v, CancellationToken.None))
                admittedCount++;

        Assert.InRange(admittedCount, 5, 20);
    }

    [Fact]
    public async Task JoinRacingQueueCleanup_RemainsDiscoverable()
    {
        var eventId = Guid.NewGuid();
        await _room.JoinAsync(eventId, Guid.NewGuid(), CancellationToken.None);

        // Drain the line so the event is de-registered from the active set.
        for (var i = 0; i < 40; i++)
        {
            await _room.AdmitBatchAsync(eventId, CancellationToken.None);
            if (!(await _room.GetActiveQueuesAsync(CancellationToken.None)).Contains(eventId))
                break;
            await Task.Delay(100);
        }
        Assert.DoesNotContain(eventId, await _room.GetActiveQueuesAsync(CancellationToken.None));

        // A NEW visitor joins after the cleanup. Because the script only de-registers an EMPTY
        // line and the join re-registers atomically, the event must be discoverable again so the
        // admitter will actually process the newcomer.
        await _room.JoinAsync(eventId, Guid.NewGuid(), CancellationToken.None);
        Assert.Contains(eventId, await _room.GetActiveQueuesAsync(CancellationToken.None));
    }

    private static List<Guid> NewVisitors(int count) =>
        Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
}

[CollectionDefinition(nameof(WaitingRoomSafetyCollection))]
public sealed class WaitingRoomSafetyCollection : ICollectionFixture<WaitingRoomApiFactory>;
