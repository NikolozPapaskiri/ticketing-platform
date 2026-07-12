using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;

namespace TicketingPlatform.Infrastructure.WaitingRoom;

/// <summary>
/// The load-leveling valve: every tick, ask the waiting room to admit whoever the GLOBAL token
/// bucket currently allows for each queued event and push live positions to the rest. The
/// admission RATE is what protects the system - demand arrives as a spike, checkout traffic
/// leaves as a steady drip.
/// Safe under replicas without leader election: the admission decision (rate + pop + grant) is
/// one atomic Redis script over a SHARED token bucket, so running this on N replicas cannot
/// exceed the configured global rate - the interval below is only how often each replica asks.
/// </summary>
public sealed class WaitingRoomAdmitter : BackgroundService
{
    private readonly RedisWaitingRoom _waitingRoom;
    private readonly IQueueBroadcaster _broadcaster;
    private readonly WaitingRoomOptions _options;
    private readonly ILogger<WaitingRoomAdmitter> _logger;

    public WaitingRoomAdmitter(RedisWaitingRoom waitingRoom, IQueueBroadcaster broadcaster,
        WaitingRoomOptions options, ILogger<WaitingRoomAdmitter> logger)
    {
        _waitingRoom = waitingRoom;
        _broadcaster = broadcaster;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await Task.Delay(_options.AdmitInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Waiting-room admission tick failed; retrying next interval");
                try { await Task.Delay(_options.AdmitInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var totalWaiting = 0L;
        foreach (var eventId in await _waitingRoom.GetActiveQueuesAsync(ct))
        {
            var (admitted, stillWaiting) = await _waitingRoom.AdmitBatchAsync(eventId, ct);

            foreach (var visitorId in admitted)
                await _broadcaster.NotifyAdmittedAsync(eventId, visitorId, ct);

            for (var i = 0; i < stillWaiting.Count; i++)
                await _broadcaster.NotifyPositionAsync(eventId, stillWaiting[i], i + 1, ct);

            // The rate of this counter is the actual global admission rate an operator alerts on.
            if (admitted.Count > 0)
            {
                TicketingMetrics.WaitingRoomAdmitted.Add(admitted.Count);
                _logger.LogInformation("Waiting room {EventId}: admitted {Count}, {Waiting} still in line",
                    eventId, admitted.Count, stillWaiting.Count);
            }

            totalWaiting += stillWaiting.Count;
        }

        // Total depth across every active queue this tick.
        TicketingMetrics.SetWaitingRoomDepth(totalWaiting);
    }
}
