using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Messaging;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Api.Features.Ops;

/// <summary>
/// Gathers the ops snapshot from the source of truth: the health-check registrations, cross-tenant
/// order/outbox counts in Postgres, the total waiting-room depth in Redis, and the dead-letter
/// queue depth in RabbitMQ. Each source is independent; a single unreachable one (the broker)
/// degrades to a null field rather than failing the whole page.
/// </summary>
public sealed class OpsSnapshotService
{
    private readonly TicketingDbContext _db;
    private readonly IWaitingRoom _waitingRoom;
    private readonly HealthCheckService _health;
    private readonly RabbitMqOptions _rabbit;
    private readonly TimeProvider _clock;
    private readonly HostRole _role;

    public OpsSnapshotService(
        TicketingDbContext db,
        IWaitingRoom waitingRoom,
        HealthCheckService health,
        IOptions<RabbitMqOptions> rabbit,
        TimeProvider clock,
        IConfiguration configuration)
    {
        _db = db;
        _waitingRoom = waitingRoom;
        _health = health;
        _rabbit = rabbit.Value;
        _clock = clock;
        _role = HostRoleExtensions.ParseHostRole(configuration["Hosting:Role"]);
    }

    public async Task<OpsSnapshot> GetAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        var health = await _health.CheckHealthAsync(ct);
        var dependencies = health.Entries
            .Select(e => new DependencyHealth(e.Key, e.Value.Status.ToString(), e.Value.Description))
            .OrderBy(d => d.Name)
            .ToList();

        // Cross-tenant (the ops view is platform-wide, so ignore the tenant filter).
        var ordersByStatus = (await _db.Orders.IgnoreQueryFilters()
                .GroupBy(o => o.Status)
                .Select(g => new { Status = g.Key, Count = g.LongCount() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Status.ToString(), x => x.Count);

        var paymentsAwaiting = await _db.Orders.IgnoreQueryFilters()
            .CountAsync(o => o.Status == OrderStatus.PendingPayment
                             && o.Hold.PaymentLeaseUntil != null
                             && o.Hold.PaymentLeaseUntil <= now, ct);

        var refundsPending = await _db.Orders.IgnoreQueryFilters()
            .CountAsync(o => o.Status == OrderStatus.RefundPending, ct);

        var outboxPending = await _db.OutboxMessages
            .CountAsync(o => o.ProcessedAt == null && o.FailedAt == null, ct);
        var outboxQuarantined = await _db.OutboxMessages
            .CountAsync(o => o.FailedAt != null, ct);
        var oldestPending = await _db.OutboxMessages
            .Where(o => o.ProcessedAt == null && o.FailedAt == null)
            .OrderBy(o => o.OccurredAt)
            .Select(o => (DateTimeOffset?)o.OccurredAt)
            .FirstOrDefaultAsync(ct);
        double? oldestPendingAge = oldestPending is null
            ? null
            : Math.Max(0, (now - oldestPending.Value).TotalSeconds);

        var waitingRoomDepth = await _waitingRoom.GetTotalWaitingAsync(ct);
        var deadLetterDepth = await TryGetDeadLetterDepthAsync(ct);

        return new OpsSnapshot(
            now,
            _role.ToString(),
            health.Status.ToString(),
            dependencies,
            waitingRoomDepth,
            paymentsAwaiting,
            refundsPending,
            outboxPending,
            outboxQuarantined,
            oldestPendingAge,
            deadLetterDepth,
            ordersByStatus);
    }

    private async Task<long?> TryGetDeadLetterDepthAsync(CancellationToken ct)
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _rabbit.HostName,
                Port = _rabbit.Port,
                UserName = _rabbit.UserName,
                Password = _rabbit.Password
            };
            await using var connection = await factory.CreateConnectionAsync(ct);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
            var queue = await channel.QueueDeclarePassiveAsync(RabbitMqTopology.DeadLetterQueue, ct);
            return queue.MessageCount;
        }
        catch
        {
            return null; // broker unreachable -> report unknown, don't fail the whole snapshot
        }
    }
}
