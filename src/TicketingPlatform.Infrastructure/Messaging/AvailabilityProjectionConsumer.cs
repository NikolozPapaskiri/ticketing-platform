using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.ReadModels;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// The CQRS projection: consumes AvailabilityChanged events, upserts the denormalized
/// EventAvailabilityView row, then pushes the fresh number to connected clients through the
/// IAvailabilityBroadcaster port (SignalR lives behind it, in the Api layer).
/// Design choice worth defending: the event carries only IDS, and the projection re-reads the
/// LIVE inventory row. Delta-carrying events applied out of order corrupt a counter forever;
/// a re-read projection is idempotent and self-healing by construction - replay, reorder, or
/// duplicate the events and the row still converges on the truth.
/// </summary>
public sealed class AvailabilityProjectionConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<AvailabilityProjectionConsumer> _logger;

    public AvailabilityProjectionConsumer(IServiceScopeFactory scopes, IOptions<RabbitMqOptions> options,
        ILogger<AvailabilityProjectionConsumer> logger)
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
                _logger.LogError(ex, "Availability projection lost the broker; reconnecting shortly");
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
                    RabbitMqTopology.AvailabilityProjectionQueue, ex, _options, _logger, ct);
            }
        };

        await channel.BasicConsumeAsync(RabbitMqTopology.AvailabilityProjectionQueue, autoAck: false, consumer, ct);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private async Task HandleAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var messageId = Guid.Parse(ea.BasicProperties.MessageId!);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        const string consumerName = nameof(AvailabilityProjectionConsumer);
        if (await db.ProcessedMessages.AnyAsync(m => m.MessageId == messageId && m.Consumer == consumerName, ct))
            return; // at-least-once: seen it, skip it

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(ea.Body.Span));
        var ticketTypeId = payload.RootElement.GetProperty("ticketTypeId").GetGuid();

        // Re-read the LIVE truth (background scope has no tenant -> IgnoreQueryFilters).
        var truth = await db.TicketTypes
            .IgnoreQueryFilters()
            .Include(tt => tt.Inventory)
            .Include(tt => tt.Event)
            .AsNoTracking()
            .FirstOrDefaultAsync(tt => tt.Id == ticketTypeId, ct);
        if (truth is null)
            return; // ticket type deleted between event and projection - nothing to project

        var view = await db.EventAvailability.IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.TicketTypeId == ticketTypeId, ct);
        if (view is null)
        {
            view = new EventAvailabilityView
            {
                TicketTypeId = ticketTypeId,
                TenantId = truth.TenantId,
                EventId = truth.EventId,
                EventName = truth.Event.Name,
                TicketTypeName = truth.Name
            };
            db.EventAvailability.Add(view);
        }

        view.Available = truth.Inventory.AvailableQuantity;
        view.Total = truth.Inventory.TotalQuantity;
        view.UpdatedAt = DateTimeOffset.UtcNow;

        // Persist the idempotent projection first, but do NOT mark the message processed yet.
        // If SignalR is temporarily unavailable, the retry must execute the broadcast again.
        await db.SaveChangesAsync(ct);

        // Push AFTER the commit - clients must never hear about state that then rolls back.
        var broadcaster = scope.ServiceProvider.GetRequiredService<IAvailabilityBroadcaster>();
        await broadcaster.BroadcastAsync(truth.EventId, ticketTypeId, view.Available, ct);

        // Only now is the whole handler complete. A crash between the broadcast and this mark can
        // produce a duplicate push on redelivery, which is safe because availability is absolute
        // state rather than a delta. Missing the push would be worse than duplicating it.
        db.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = messageId,
            Consumer = consumerName,
            ProcessedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }
}
