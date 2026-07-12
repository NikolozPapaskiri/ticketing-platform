using RabbitMQ.Client;
using TicketingPlatform.Infrastructure.Messaging;

namespace TicketingPlatform.IntegrationTests;

/// <summary>Injects one transport loss immediately before the real publisher can await a confirm.</summary>
internal sealed class FailOnceOutboxPublisher : IOutboxPublisher
{
    private readonly RabbitMqOutboxPublisher _inner;
    private int _failuresRemaining;

    public FailOnceOutboxPublisher(RabbitMqOutboxPublisher inner) => _inner = inner;

    public void FailNextPublish() => Interlocked.Exchange(ref _failuresRemaining, 1);

    public Task PublishAsync(string routingKey, BasicProperties properties, ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _failuresRemaining, 0) == 1)
            throw new IOException("Deterministic broker transport loss before publisher confirmation.");

        return _inner.PublishAsync(routingKey, properties, body, ct);
    }
}
