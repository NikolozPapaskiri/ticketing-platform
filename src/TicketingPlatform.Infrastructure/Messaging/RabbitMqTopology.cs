using RabbitMQ.Client;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// The single source of truth for the broker topology: the topic exchange, the dead-letter
/// exchange + parked queue, and every consumer queue with its bindings. Declared once by the
/// topology initializer BEFORE the dispatcher publishes, so a publish can never race ahead of the
/// bindings and vanish. RabbitMQ declarations are idempotent, so consumers may re-declare safely.
///
/// The routing keys here are the exhaustive set of event types the outbox publishes - every one
/// has a binding, so with mandatory publishing an unroutable message signals a real topology gap,
/// not a routine drop. (OrderRefunded is bound to notifications; without it a refund event would
/// be unroutable.)
/// </summary>
public static class RabbitMqTopology
{
    public const string DeadLetterQueue = "ticketing-dead-letter";
    public const string NotificationsQueue = "notifications";
    public const string AvailabilityProjectionQueue = "availability-projection";
    public const string TicketIssuerQueue = "ticket-issuer";

    public static string RetryQueueName(string consumerQueue, string routingKey) =>
        $"{consumerQueue}.retry.{routingKey}";

    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        // Exchanges: the domain topic exchange and the fan-out dead-letter exchange.
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);

        // Poison messages are parked here instead of looping forever.
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(DeadLetterQueue, options.DeadLetterExchange, routingKey: string.Empty, cancellationToken: ct);

        var deadLetter = new Dictionary<string, object?> { ["x-dead-letter-exchange"] = options.DeadLetterExchange };

        await DeclareBoundQueueAsync(channel, NotificationsQueue, options, deadLetter, ct, "OrderConfirmed", "OrderRefunded");
        await DeclareBoundQueueAsync(channel, AvailabilityProjectionQueue, options, deadLetter, ct, "AvailabilityChanged");
        await DeclareBoundQueueAsync(channel, TicketIssuerQueue, options, deadLetter, ct, "OrderConfirmed");
    }

    private static async Task DeclareBoundQueueAsync(IChannel channel, string queue, RabbitMqOptions options,
        Dictionary<string, object?> arguments, CancellationToken ct, params string[] routingKeys)
    {
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: arguments, cancellationToken: ct);
        foreach (var key in routingKeys)
        {
            await channel.QueueBindAsync(queue, options.Exchange, routingKey: key, cancellationToken: ct);

            // One retry queue per consumer + event type prevents a TicketIssuer retry from
            // fanning out to the Notification consumer. Expiry routes the same message back to
            // the domain exchange under its original event key.
            var retryArguments = new Dictionary<string, object?>
            {
                ["x-message-ttl"] = Math.Max(1, options.ConsumerRetryDelayMilliseconds),
                ["x-dead-letter-exchange"] = options.Exchange,
                ["x-dead-letter-routing-key"] = key
            };
            await channel.QueueDeclareAsync(RetryQueueName(queue, key), durable: true,
                exclusive: false, autoDelete: false, arguments: retryArguments, cancellationToken: ct);
        }
    }
}
