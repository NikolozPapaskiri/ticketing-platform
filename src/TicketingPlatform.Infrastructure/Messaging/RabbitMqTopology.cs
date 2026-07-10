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

    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        // Exchanges: the domain topic exchange and the fan-out dead-letter exchange.
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);

        // Poison messages are parked here instead of looping forever.
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(DeadLetterQueue, options.DeadLetterExchange, routingKey: string.Empty, cancellationToken: ct);

        var deadLetter = new Dictionary<string, object?> { ["x-dead-letter-exchange"] = options.DeadLetterExchange };

        await DeclareBoundQueueAsync(channel, NotificationsQueue, options.Exchange, deadLetter, ct, "OrderConfirmed", "OrderRefunded");
        await DeclareBoundQueueAsync(channel, AvailabilityProjectionQueue, options.Exchange, deadLetter, ct, "AvailabilityChanged");
        await DeclareBoundQueueAsync(channel, TicketIssuerQueue, options.Exchange, deadLetter, ct, "OrderConfirmed");
    }

    private static async Task DeclareBoundQueueAsync(IChannel channel, string queue, string exchange,
        Dictionary<string, object?> arguments, CancellationToken ct, params string[] routingKeys)
    {
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: arguments, cancellationToken: ct);
        foreach (var key in routingKeys)
            await channel.QueueBindAsync(queue, exchange, routingKey: key, cancellationToken: ct);
    }
}
