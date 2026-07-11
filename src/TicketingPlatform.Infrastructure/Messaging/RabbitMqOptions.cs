namespace TicketingPlatform.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string UserName { get; init; } = "guest";
    public string Password { get; init; } = "guest";

    /// <summary>Topic exchange all domain events flow through; the routing key is the event type.</summary>
    public string Exchange { get; init; } = "ticketing-events";

    /// <summary>Where poison messages are parked instead of looping forever.</summary>
    public string DeadLetterExchange { get; init; } = "ticketing-dlx";

    /// <summary>Initial delay before retrying a publish rejected or returned by RabbitMQ.</summary>
    public int OutboxRetryBaseSeconds { get; init; } = 5;

    /// <summary>Maximum publish attempts before the row is quarantined for operator action.</summary>
    public int OutboxMaxAttempts { get; init; } = 10;
}
