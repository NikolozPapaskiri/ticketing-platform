using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// PR 3 - outbox delivery must be honest: a row is marked processed ONLY after RabbitMQ confirms
/// the publish, and an unroutable message (no binding) is left recoverable in the outbox rather
/// than silently dropped. Runs against real RabbitMQ.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class OutboxDeliveryTests
{
    private readonly TicketingApiFactory _factory;

    public OutboxDeliveryTests(TicketingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Outbox_UnroutableMessage_RemainsUnprocessed()
    {
        var probeId = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = probeId,
                Type = "UnroutableProbe", // no queue binds this key => mandatory publish is returned
                TenantId = Guid.NewGuid(),
                Payload = "{}",
                OccurredAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        try
        {
            // Wait until the dispatcher has actually ATTEMPTED it, then assert it was NOT marked
            // processed. With confirms + mandatory routing a returned message can never be
            // completed - it stays in the outbox for retry. (The old fire-and-forget publish
            // would have marked it processed and lost it.)
            OutboxMessage? row = null;
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(500);
                row = await ReadAsync(probeId);
                if (row is { Attempts: > 0 }) break;
            }

            Assert.NotNull(row);
            Assert.True(row!.Attempts > 0, "the dispatcher never attempted the probe within 10s");
            Assert.Null(row.ProcessedAt); // attempted, never confirmed => never processed
        }
        finally
        {
            // Stop the dispatcher retrying the probe for the rest of the shared run.
            await WithDbAsync(db => db.OutboxMessages.Where(m => m.Id == probeId).ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task Outbox_UnroutableMessage_IsBackedOffAfterFailure()
    {
        var probeId = await InsertProbeAsync();

        try
        {
            var firstFailure = await WaitForAttemptAsync(probeId);
            var attemptsAfterFailure = firstFailure.Attempts;

            // The test factory configures a 30-second base delay. A failed row must therefore
            // not be selected again by the dispatcher's one-second polling loop during this
            // observation window. The current implementation retries it on every poll.
            await Task.Delay(TimeSpan.FromSeconds(3));

            var afterBackoffWindow = await ReadAsync(probeId);
            Assert.NotNull(afterBackoffWindow);
            Assert.Equal(attemptsAfterFailure, afterBackoffWindow!.Attempts);
            Assert.Null(afterBackoffWindow.ProcessedAt);
        }
        finally
        {
            await DeleteAsync(probeId);
        }
    }

    [Fact]
    public async Task Outbox_MessageAtAttemptCap_IsNotDispatchedAgain()
    {
        // The configured cap is two. Seed a failed message at the cap and prove the dispatcher
        // does not keep publishing it forever. A later implementation will make the terminal
        // failure operator-visible; this test first pins the bounded-delivery invariant.
        var probeId = await InsertProbeAsync(attempts: 2);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            var row = await ReadAsync(probeId);
            Assert.NotNull(row);
            Assert.Equal(2, row!.Attempts);
            Assert.Null(row.ProcessedAt);
        }
        finally
        {
            await DeleteAsync(probeId);
        }
    }

    [Fact]
    public async Task Outbox_UnroutableMessage_IsQuarantinedWhenAttemptBudgetIsExhausted()
    {
        // One failed publish reaches the test factory's configured cap of two.
        var probeId = await InsertProbeAsync(attempts: 1);

        try
        {
            OutboxMessage? failed = null;
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(500);
                failed = await ReadAsync(probeId);
                if (failed?.FailedAt is not null)
                    break;
            }

            Assert.NotNull(failed);
            Assert.NotNull(failed!.FailedAt);
            Assert.Equal(2, failed.Attempts);
            Assert.Null(failed.ProcessedAt);
            Assert.Null(failed.NextAttemptAt);
            Assert.False(string.IsNullOrWhiteSpace(failed.LastError));
        }
        finally
        {
            await DeleteAsync(probeId);
        }
    }

    [Fact]
    public async Task Outbox_PublishesVersionedIntegrationEventEnvelope()
    {
        await using var connection = await CreateRabbitConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var queue = await channel.QueueDeclareAsync(queue: string.Empty, durable: false,
            exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue.QueueName, "ticketing-events", "AvailabilityChanged");

        var messageId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var ticketTypeId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        await WithDbAsync(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = messageId,
                Type = "AvailabilityChanged",
                CorrelationId = "envelope-contract-test",
                Payload = JsonSerializer.Serialize(new { tenantId, eventId, ticketTypeId }),
                OccurredAt = occurredAt
            });
            await db.SaveChangesAsync();
        });

        BasicGetResult? delivery = null;
        for (var i = 0; i < 40 && delivery is null; i++)
        {
            delivery = await channel.BasicGetAsync(queue.QueueName, autoAck: false);
            if (delivery is null)
                await Task.Delay(250);
        }

        Assert.NotNull(delivery);
        using var body = JsonDocument.Parse(Encoding.UTF8.GetString(delivery!.Body.Span));
        var root = body.RootElement;
        Assert.Equal(messageId, root.GetProperty("messageId").GetGuid());
        Assert.Equal("AvailabilityChanged", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(tenantId, root.GetProperty("tenantId").GetGuid());
        Assert.Equal("envelope-contract-test", root.GetProperty("correlationId").GetString());
        Assert.Equal(occurredAt.ToUnixTimeMilliseconds(),
            root.GetProperty("occurredAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.Equal(ticketTypeId, root.GetProperty("payload").GetProperty("ticketTypeId").GetGuid());
        Assert.Equal(messageId.ToString(), delivery.BasicProperties.MessageId);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);

        await DeleteAsync(messageId);
    }

    private async Task<Guid> InsertProbeAsync(int attempts = 0)
    {
        var probeId = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = probeId,
                Type = "UnroutableProbe",
                TenantId = Guid.NewGuid(),
                Payload = "{}",
                OccurredAt = DateTimeOffset.UtcNow,
                Attempts = attempts
            });
            await db.SaveChangesAsync();
        });
        return probeId;
    }

    private async Task<OutboxMessage> WaitForAttemptAsync(Guid id)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            var row = await ReadAsync(id);
            if (row is { Attempts: > 0 })
                return row;
        }

        throw new Xunit.Sdk.XunitException("the dispatcher never attempted the probe within 10s");
    }

    private Task DeleteAsync(Guid id) =>
        WithDbAsync(db => db.OutboxMessages.Where(m => m.Id == id).ExecuteDeleteAsync());

    private async Task<IConnection> CreateRabbitConnectionAsync()
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

    private async Task<OutboxMessage?> ReadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        return await db.OutboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
    }

    private async Task WithDbAsync(Func<TicketingDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        await action(db);
    }
}
