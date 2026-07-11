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
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LockedBy { get; set; }

    /// <summary>Earliest time the dispatcher may retry this row after a failed publication.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Set when the configured publish-attempt budget is exhausted.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>Last broker failure retained for operator diagnosis; never contains the payload.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// W3C traceparent of the request that produced this event. The dispatcher polls outside
    /// any request context, so without this column the trace would end at the outbox write.
    /// </summary>
    public string? TraceParent { get; set; }
}

/// <summary>
/// Consumer-side dedupe: at-least-once delivery means "have I seen this MessageId already?"
/// Keyed per CONSUMER (composite PK): one event fans out to several consumers (OrderConfirmed
/// drives both the notification writer and the ticket issuer), and each must track its own
/// progress - a shared mark would make whichever consumer runs second silently skip.
/// </summary>
public class ProcessedMessage
{
    public Guid MessageId { get; set; }
    public required string Consumer { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
