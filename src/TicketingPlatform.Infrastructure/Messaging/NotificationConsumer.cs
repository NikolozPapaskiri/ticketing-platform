using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// Consumes OrderConfirmed events and writes a Notification record. Demonstrates the two
/// non-negotiables of at-least-once messaging:
///  - IDEMPOTENCY: dedupe by MessageId against the ProcessedMessages table, checked and
///    recorded in the SAME transaction as the side effect - a redelivered message is a no-op.
///  - FAILURE HANDLING: malformed messages park immediately; operational failures pass through
///    the shared durable retry queues and are dead-lettered after a configured attempt bound.
/// </summary>
public sealed class NotificationConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<NotificationConsumer> _logger;

    public NotificationConsumer(IServiceScopeFactory scopes, IOptions<RabbitMqOptions> options, ILogger<NotificationConsumer> logger)
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
                await RunConsumerLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification consumer lost the broker; reconnecting shortly");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunConsumerLoopAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password
        };

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

        await RabbitMqTopology.DeclareAsync(channel, _options, ct);
        await channel.BasicQosAsync(0, prefetchCount: 1, global: false, ct); // one message at a time

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                await HandleAsync(ea, ct);
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
            }
            catch (Exception ex)
            {
                await ConsumerRetryPolicy.HandleFailureAsync(channel, ea,
                    RabbitMqTopology.NotificationsQueue, ex, _options, _logger, ct);
            }
        };

        await channel.BasicConsumeAsync(RabbitMqTopology.NotificationsQueue, autoAck: false, consumer, ct);

        // Hold the connection open until shutdown; messages arrive on the consumer callback.
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private async Task HandleAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        // Rejoin the trace that the dispatcher stamped into the headers (Consumer span):
        // the same trace id now spans HTTP request -> outbox -> broker -> this handler.
        string? traceParent = null;
        if (ea.BasicProperties.Headers?.TryGetValue("traceparent", out var raw) == true && raw is byte[] bytes)
            traceParent = Encoding.UTF8.GetString(bytes);
        var eventType = ea.RoutingKey;
        using var activity = MessagingDiagnostics.Source.StartActivity(
            $"consume {eventType}", System.Diagnostics.ActivityKind.Consumer, traceParent);

        var envelope = IntegrationEventEnvelopeCodec.Read(ea);
        var messageId = envelope.MessageId;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        // IDEMPOTENCY GATE: seen it before (by THIS consumer)? At-least-once delivered twice - skip.
        const string consumerName = nameof(NotificationConsumer);
        if (await db.ProcessedMessages.AnyAsync(m => m.MessageId == messageId && m.Consumer == consumerName, ct))
            return;

        var details = eventType switch
        {
            IntegrationEventNames.OrderConfirmed => ConfirmedDetails(envelope),
            IntegrationEventNames.OrderRefunded => RefundedDetails(envelope),
            _ => throw new IntegrationEventContractException(
                $"Notification consumer does not support event type '{eventType}'.")
        };

        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            TenantId = envelope.TenantId,
            Type = eventType,
            Message = $"Order {details.OrderId} {details.Verb} for {details.CustomerEmail}: " +
                      $"{details.Amount} {details.Currency}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId, Consumer = consumerName, ProcessedAt = DateTimeOffset.UtcNow });

        // Side effect + dedupe mark in ONE transaction: they live or die together.
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Notification written for message {MessageId}", messageId);
    }

    private static NotificationDetails ConfirmedDetails(IntegrationEventEnvelope envelope)
    {
        var message = IntegrationEventEnvelopeCodec.ReadPayload<OrderConfirmedIntegrationEvent>(envelope);
        return new(message.OrderId, message.CustomerEmail, message.Amount, message.Currency, "confirmed");
    }

    private static NotificationDetails RefundedDetails(IntegrationEventEnvelope envelope)
    {
        var message = IntegrationEventEnvelopeCodec.ReadPayload<OrderRefundedIntegrationEvent>(envelope);
        return new(message.OrderId, message.CustomerEmail, message.Amount, message.Currency, "refunded");
    }

    private sealed record NotificationDetails(
        Guid OrderId, string CustomerEmail, decimal Amount, string Currency, string Verb);
}
