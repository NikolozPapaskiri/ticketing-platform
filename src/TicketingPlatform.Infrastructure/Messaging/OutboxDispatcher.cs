using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System.Text.Json;
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
    private const int BatchSize = 20;

    private readonly IServiceScopeFactory _scopes;
    private readonly RabbitMqOptions _options;
    private readonly IOutboxPublisher _publisher;
    private readonly ILogger<OutboxDispatcher> _logger;

    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private int MaxAttempts => Math.Max(1, _options.OutboxMaxAttempts);
    private TimeSpan LockDuration => TimeSpan.FromSeconds(Math.Max(1, _options.OutboxLockSeconds));

    public OutboxDispatcher(IServiceScopeFactory scopes, IOptions<RabbitMqOptions> options,
        IOutboxPublisher publisher, ILogger<OutboxDispatcher> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _publisher = publisher;
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
                // silently strands every event in the outbox. The publisher rebuilds a broken
                // connection/channel on the next tick; an unconfirmed claim expires for retry.
                _logger.LogError(ex, "Outbox dispatch failed; retrying shortly");
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

        var confirmed = 0;
        foreach (var message in batch)
        {
            // Continue the originating request's trace across the async boundary (Producer span).
            using var activity = MessagingDiagnostics.Source.StartActivity(
                $"publish {message.Type}", System.Diagnostics.ActivityKind.Producer, message.TraceParent);

            var properties = new BasicProperties
            {
                MessageId = message.Id.ToString(), // the consumer's dedupe handle
                CorrelationId = message.CorrelationId,
                ContentType = "application/json",
                Type = message.Type,
                Persistent = true,                 // survive a broker restart
                Headers = activity is null
                    ? null
                    : new Dictionary<string, object?> { ["traceparent"] = activity.Id }
            };

            try
            {
                var body = IntegrationEventEnvelopeCodec.Serialize(message);
                // Publisher confirms + tracking are enabled on the channel, so this await does not
                // complete until RabbitMQ has ACKED the publish. mandatory:true means an unroutable
                // message (no binding) is RETURNED and surfaces as a PublishException instead of
                // being silently dropped. A row is marked processed ONLY after that confirmation -
                // the outbox never lies about delivery.
                await _publisher.PublishAsync(message.Type, properties, body, ct);

                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.Attempts++;
                message.LockedBy = null;
                message.LockedUntil = null;
                message.NextAttemptAt = null;
                message.LastError = null;
                confirmed++;
            }
            catch (PublishException ex)
            {
                // Unroutable (no binding) or nacked by the broker: leave the row UNPROCESSED.
                // Schedule retries so a permanent topology defect cannot create a one-second hot
                // loop. Exhausted rows are quarantined for operator diagnosis and explicit replay.
                var failedAt = DateTimeOffset.UtcNow;
                message.Attempts++;
                message.LockedBy = null;
                message.LockedUntil = null;
                message.LastError = Truncate(ex.Message, 2000);

                if (message.Attempts >= MaxAttempts)
                {
                    message.FailedAt = failedAt;
                    message.NextAttemptAt = null;
                    _logger.LogError(ex,
                        "Outbox message {MessageId} ({Type}) exhausted {Attempts} publish attempts and was quarantined",
                        message.Id, message.Type, message.Attempts);
                }
                else
                {
                    message.NextAttemptAt = failedAt.Add(CalculateRetryDelay(message.Attempts));
                    _logger.LogWarning(ex,
                        "Outbox message {MessageId} ({Type}) was not confirmed (returned={Returned}); retry {Attempt}/{MaxAttempts} after {NextAttemptAt}",
                        message.Id, message.Type, ex.IsReturn, message.Attempts,
                        MaxAttempts, message.NextAttemptAt);
                }
            }
            catch (Exception ex) when (ex is JsonException or IntegrationEventContractException)
            {
                // An invalid row is an internal poison message. Retrying cannot repair its JSON or
                // metadata, so quarantine it immediately instead of blocking the polling batch.
                message.Attempts++;
                message.FailedAt = DateTimeOffset.UtcNow;
                message.NextAttemptAt = null;
                message.LockedBy = null;
                message.LockedUntil = null;
                message.LastError = Truncate(ex.Message, 2000);
                _logger.LogError(ex,
                    "Outbox message {MessageId} ({Type}) has an invalid integration-event contract and was quarantined",
                    message.Id, message.Type);
            }
        }

        await db.SaveChangesAsync(ct);
        if (confirmed > 0)
            TicketingMetrics.OutboxPublished.Add(confirmed);
        _logger.LogInformation("Dispatched {Confirmed}/{Total} outbox message(s)", confirmed, batch.Count);
    }

    private async Task<List<TicketingPlatform.Infrastructure.Outbox.OutboxMessage>> ClaimBatchAsync(
        TicketingDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var maxAttempts = MaxAttempts;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT * FROM "OutboxMessages"
                WHERE "Id" IN (
                    SELECT "Id" FROM "OutboxMessages"
                    WHERE "ProcessedAt" IS NULL
                      AND "FailedAt" IS NULL
                      AND "Attempts" < {maxAttempts}
                      AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {now})
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

    private TimeSpan CalculateRetryDelay(int attempts)
    {
        var baseSeconds = Math.Max(1, _options.OutboxRetryBaseSeconds);
        var exponent = Math.Min(Math.Max(0, attempts - 1), 10);
        var exponentialSeconds = Math.Min(baseSeconds * Math.Pow(2, exponent), 3600);
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromSeconds(exponentialSeconds * jitter);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

}
