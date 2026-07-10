using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
