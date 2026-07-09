namespace TicketingPlatform.Infrastructure.Outbox;

/// <summary>
/// The transactional outbox row - the answer to the dual-write problem. You cannot atomically
/// write to Postgres AND publish to RabbitMQ; a crash between the two either loses the event
/// or announces a change that never committed. So the event is written HERE, in the same
/// database transaction as the state change, and the OutboxDispatcher publishes it afterwards.
/// At-least-once by construction (a crash after publish but before marking processed
/// re-publishes), which is why every consumer must be idempotent.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Event type, doubles as the RabbitMQ routing key (e.g. "OrderConfirmed").</summary>
    public required string Type { get; set; }

    /// <summary>JSON payload of the event.</summary>
    public required string Payload { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
}

/// <summary>Consumer-side dedupe: at-least-once delivery means "have I seen this MessageId already?"</summary>
public class ProcessedMessage
{
    public Guid MessageId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
