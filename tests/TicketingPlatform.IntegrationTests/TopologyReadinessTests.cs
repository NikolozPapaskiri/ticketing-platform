using RabbitMQ.Client;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Infrastructure.Messaging;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// PR 3 - topology must be READY before the first publish. The RabbitMqTopologyInitializer is a
/// plain IHostedService registered ahead of the dispatcher, so by the time the host is up its
/// StartAsync has completed and the whole structure exists. Passive declares fail if an entity is
/// absent, so this proves every exchange, main queue, retry queue, and the dead-letter queue were
/// created up front rather than left to a consumer-startup race. (Binding correctness - a real key
/// routes, an unbound key is returned - is proven by Outbox_UnroutableMessage_RemainsUnprocessed
/// and the happy-path delivery tests.)
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class TopologyReadinessTests
{
    private readonly TicketingApiFactory _factory;

    public TopologyReadinessTests(TicketingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Topology_IsReadyBeforeFirstPublish()
    {
        await using var connection = await CreateConnectionAsync();

        await AssertExchangeExistsAsync(connection, "ticketing-events");
        await AssertExchangeExistsAsync(connection, "ticketing-dlx");

        string[] queues =
        [
            RabbitMqTopology.DeadLetterQueue,
            RabbitMqTopology.NotificationsQueue,
            RabbitMqTopology.AvailabilityProjectionQueue,
            RabbitMqTopology.TicketIssuerQueue,
            RabbitMqTopology.RetryQueueName(RabbitMqTopology.NotificationsQueue, IntegrationEventNames.OrderConfirmed),
            RabbitMqTopology.RetryQueueName(RabbitMqTopology.NotificationsQueue, IntegrationEventNames.OrderRefunded),
            RabbitMqTopology.RetryQueueName(RabbitMqTopology.AvailabilityProjectionQueue, IntegrationEventNames.AvailabilityChanged),
            RabbitMqTopology.RetryQueueName(RabbitMqTopology.TicketIssuerQueue, IntegrationEventNames.OrderConfirmed),
        ];

        foreach (var queue in queues)
            await AssertQueueExistsAsync(connection, queue);
    }

    private async Task AssertExchangeExistsAsync(IConnection connection, string exchange)
    {
        // Fresh channel per check: a failed passive declare closes the channel it ran on.
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclarePassiveAsync(exchange); // throws if the exchange is absent
    }

    private async Task AssertQueueExistsAsync(IConnection connection, string queue)
    {
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclarePassiveAsync(queue); // throws if the queue is absent
    }

    private async Task<IConnection> CreateConnectionAsync()
    {
        var factory = new ConnectionFactory
        {
            HostName = _factory.RabbitHost,
            Port = _factory.RabbitPort,
            UserName = "ticketing",
            Password = "ticketing"
        };
        return await factory.CreateConnectionAsync();
    }
}
