using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;

namespace TicketingPlatform.Infrastructure.WaitingRoom;

/// <summary>
/// The load-leveling valve: every tick, admit AdmitBatchSize visitors per queued event and
/// push live positions to the rest. The admission RATE is what protects the system - demand
/// arrives as a spike, checkout traffic leaves as a steady drip.
/// Safe under replicas without leader election because ZPOPMIN is atomic: two admitters
/// admit disjoint visitors (the effective rate doubles, which is a tuning note, not a bug).
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
        foreach (var eventId in await _waitingRoom.GetActiveQueuesAsync(ct))
        {
            var (admitted, stillWaiting) = await _waitingRoom.AdmitBatchAsync(eventId, ct);

            foreach (var visitorId in admitted)
                await _broadcaster.NotifyAdmittedAsync(eventId, visitorId, ct);

            for (var i = 0; i < stillWaiting.Count; i++)
                await _broadcaster.NotifyPositionAsync(eventId, stillWaiting[i], i + 1, ct);

            if (admitted.Count > 0)
                _logger.LogInformation("Waiting room {EventId}: admitted {Count}, {Waiting} still in line",
                    eventId, admitted.Count, stillWaiting.Count);
        }
    }
}
