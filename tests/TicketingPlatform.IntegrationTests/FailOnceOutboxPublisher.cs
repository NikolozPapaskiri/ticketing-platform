using RabbitMQ.Client;
using TicketingPlatform.Infrastructure.Messaging;

namespace TicketingPlatform.IntegrationTests;

/// <summary>Injects one transport loss on either side of a real publisher confirmation.</summary>
internal sealed class FailOnceOutboxPublisher : IOutboxPublisher
{
    private readonly RabbitMqOutboxPublisher _inner;
    private int _failurePhase;

    public FailOnceOutboxPublisher(RabbitMqOutboxPublisher inner) => _inner = inner;

    public void FailNextPublishBeforeConfirm() => Interlocked.Exchange(ref _failurePhase, 1);
    public void FailNextPublishAfterConfirm() => Interlocked.Exchange(ref _failurePhase, 2);

    public Task PublishAsync(string routingKey, BasicProperties properties, ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        var phase = Interlocked.Exchange(ref _failurePhase, 0);
        if (phase == 1)
            throw new IOException("Deterministic broker transport loss before publisher confirmation.");

        return PublishAfterPossibleConfirmationAsync(phase, routingKey, properties, body, ct);
    }

    private async Task PublishAfterPossibleConfirmationAsync(int phase, string routingKey,
        BasicProperties properties, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        await _inner.PublishAsync(routingKey, properties, body, ct);
        if (phase == 2)
            throw new IOException("Deterministic process loss after publisher confirmation.");
    }
}
