using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Text;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// The outbox's second half: polls unprocessed rows and publishes them to RabbitMQ. This
/// gives AT-LEAST-ONCE delivery - a crash after publishing but before marking the row
/// processed re-publishes on restart - which is exactly why consumers dedupe by MessageId.
/// BackgroundService rules honored: a scope per iteration (DbContext is scoped; a singleton
/// service must never capture one - the captive-dependency bug), and the stopping token is
/// respected so shutdown is graceful.
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(30);
    private const int BatchSize = 20;

    private readonly IServiceScopeFactory _scopes;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    private IConnection? _connection;
    private IChannel? _channel;
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public OutboxDispatcher(IServiceScopeFactory scopes, IOptions<RabbitMqOptions> options, ILogger<OutboxDispatcher> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                // Broker down / DB blip: log, back off, keep living. A dispatcher that dies
                // silently strands every event in the outbox.
                _logger.LogError(ex, "Outbox dispatch failed; retrying shortly");
                _channel = null; _connection = null;
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var batch = await ClaimBatchAsync(db, ct);

        if (batch.Count == 0)
            return;

        var channel = await EnsureChannelAsync(ct);

        foreach (var message in batch)
        {
            // Continue the originating request's trace across the async boundary (Producer span).
            using var activity = MessagingDiagnostics.Source.StartActivity(
                $"publish {message.Type}", System.Diagnostics.ActivityKind.Producer, message.TraceParent);

            var properties = new BasicProperties
            {
                MessageId = message.Id.ToString(), // the consumer's dedupe handle
                Persistent = true,                 // survive a broker restart
                Headers = activity is null
                    ? null
                    : new Dictionary<string, object?> { ["traceparent"] = activity.Id }
            };

            await channel.BasicPublishAsync(
                exchange: _options.Exchange,
                routingKey: message.Type,
                mandatory: false,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(message.Payload),
                cancellationToken: ct);

            message.ProcessedAt = DateTimeOffset.UtcNow;
            message.Attempts++;
            message.LockedBy = null;
            message.LockedUntil = null;
        }

        await db.SaveChangesAsync(ct);
        TicketingMetrics.OutboxPublished.Add(batch.Count);
        _logger.LogInformation("Dispatched {Count} outbox message(s)", batch.Count);
    }

    private async Task<List<TicketingPlatform.Infrastructure.Outbox.OutboxMessage>> ClaimBatchAsync(
        TicketingDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT * FROM "OutboxMessages"
                WHERE "Id" IN (
                    SELECT "Id" FROM "OutboxMessages"
                    WHERE "ProcessedAt" IS NULL
                      AND ("LockedUntil" IS NULL OR "LockedUntil" <= {now})
                    ORDER BY "OccurredAt"
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED
                )
                ORDER BY "OccurredAt"
                """)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            message.LockedBy = _workerId;
            message.LockedUntil = now.Add(LockDuration);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch;
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password
        };

        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
        await _channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        return _channel;
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
