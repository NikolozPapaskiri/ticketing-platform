using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// Second consumer of OrderConfirmed (fan-out: same event, own queue, own dedupe row):
/// renders the ticket PDF, stores it via the IFileStorage port, records the Ticket row.
/// Issuing tickets asynchronously keeps the checkout latency free of PDF rendering - the
/// buyer's 201 never waits for a document.
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
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(_options.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        // Own queue on the same routing key as the notification consumer: the topic exchange
        // copies OrderConfirmed into BOTH queues - that is how fan-out works, not shared reads.
        await channel.QueueDeclareAsync("ticket-issuer", durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = _options.DeadLetterExchange },
            cancellationToken: ct);
        await channel.QueueBindAsync("ticket-issuer", _options.Exchange, routingKey: "OrderConfirmed", cancellationToken: ct);

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
                _logger.LogError(ex, "Poison message {MessageId} in ticket issuer; dead-lettering", ea.BasicProperties.MessageId);
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            }
        };

        await channel.BasicConsumeAsync("ticket-issuer", autoAck: false, consumer, ct);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private async Task HandleAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var messageId = Guid.Parse(ea.BasicProperties.MessageId!);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        const string consumerName = nameof(TicketIssuerConsumer);
        if (await db.ProcessedMessages.AnyAsync(m => m.MessageId == messageId && m.Consumer == consumerName, ct))
            return;

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(ea.Body.Span));
        var orderId = payload.RootElement.GetProperty("orderId").GetGuid();

        if (await db.Tickets.IgnoreQueryFilters().AnyAsync(t => t.OrderId == orderId, ct))
        {
            db.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId, Consumer = consumerName, ProcessedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            return;
        }

        // Re-read the full graph from the source of truth (background scope: IgnoreQueryFilters).
        var order = await db.Orders
            .IgnoreQueryFilters()
            .Include(o => o.Hold).ThenInclude(h => h.TicketType).ThenInclude(tt => tt.Event)
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
            order.Hold.TicketType.Event.StartsAt,
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
