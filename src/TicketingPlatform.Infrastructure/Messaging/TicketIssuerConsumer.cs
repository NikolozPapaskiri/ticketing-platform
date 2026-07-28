using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Persistence.Scopes;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// Second consumer of OrderConfirmed (fan-out: same event, own queue, own dedupe row):
/// renders the ticket PDF, stores it via the IFileStorage port, records the Ticket row.
/// Issuing tickets asynchronously keeps the checkout latency free of PDF rendering - the
/// buyer's 201 never waits for a document. Transient storage/DB failures use the shared durable
/// retry policy; malformed events and exhausted attempts are parked in the dead-letter queue.
/// </summary>
public sealed class TicketIssuerConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<TicketIssuerConsumer> _logger;

    public TicketIssuerConsumer(IServiceScopeFactory scopes, IOptions<RabbitMqOptions> options, ILogger<TicketIssuerConsumer> logger)
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
                _logger.LogError(ex, "Ticket issuer lost the broker; reconnecting shortly");
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

        await channel.BasicQosAsync(0, prefetchCount: 1, global: false, ct);

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
                    RabbitMqTopology.TicketIssuerQueue, ex, _options, _logger, ct);
            }
        };

        await channel.BasicConsumeAsync(RabbitMqTopology.TicketIssuerQueue, autoAck: false, consumer, ct);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private async Task HandleAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var envelope = IntegrationEventEnvelopeCodec.Read(ea);
        var messageId = envelope.MessageId;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var system = scope.ServiceProvider.GetRequiredService<SystemScope>();

        const string consumerName = nameof(TicketIssuerConsumer);
        if (await db.ProcessedMessages.AnyAsync(m => m.MessageId == messageId && m.Consumer == consumerName, ct))
            return;

        var message = IntegrationEventEnvelopeCodec.ReadPayload<OrderConfirmedIntegrationEvent>(envelope);
        var orderId = message.OrderId;

        if (await system.Of<Ticket>().AnyAsync(t => t.OrderId == orderId, ct))
        {
            db.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId, Consumer = consumerName, ProcessedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            return;
        }

        // Re-read the full graph from the source of truth (background scope: no tenant).
        var order = await system.Of<Order>()
            .Include(o => o.Hold).ThenInclude(h => h.TicketType).ThenInclude(tt => tt.Event)
            .Include(o => o.Hold).ThenInclude(h => h.TicketType).ThenInclude(tt => tt.Performance)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
            return;

        var generator = scope.ServiceProvider.GetRequiredService<ITicketDocumentGenerator>();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var pdf = generator.Generate(new TicketDocumentData(
            order.Id,
            order.Hold.TicketType.Event.Name,
            order.Hold.TicketType.Event.VenueName,
            // The date printed is the date the ticket admits you to - the performance's, never a
            // summary of the run's. Both navigations are Included above; the rule itself lives on
            // TicketType so it can be reasoned about (and tested) without a broker.
            order.Hold.TicketType.AdmissionDate,
            order.Hold.TicketType.Name,
            order.Hold.Quantity,
            order.CustomerEmail,
            code,
            order.Amount,
            order.Currency));

        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var path = $"tickets/{order.TenantId}/{order.Id}.pdf";
        await storage.SaveAsync(path, pdf, ct);

        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(),
            TenantId = order.TenantId,
            OrderId = order.Id,
            Code = code,
            FilePath = path,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId, Consumer = consumerName, ProcessedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Issued ticket PDF for order {OrderId}", orderId);
    }
}
