using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Messaging;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// PR 6 §6.4 - the retention sweep prunes the bookkeeping tables that otherwise grow forever
/// (delivered outbox rows, dedupe marks, completed idempotency records, dead refresh tokens) while
/// leaving anything still live: recent rows, an unprocessed outbox row, an in-progress idempotency
/// record, and an unexpired token.
/// </summary>
[Collection(nameof(ApiCollection))]
public class RetentionServiceTests
{
    private readonly TicketingApiFactory _factory;
    public RetentionServiceTests(TicketingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Sweep_PrunesOldBookkeeping_ButKeepsLiveRows()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-40); // well past every retention window (7/7/7/3 days)
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var outboxOld = Guid.NewGuid();
        var outboxRecent = Guid.NewGuid();
        var outboxUnprocessed = Guid.NewGuid();
        var dedupeOld = Guid.NewGuid();
        var dedupeRecent = Guid.NewGuid();
        var idemDoneOld = Guid.NewGuid();
        var idemDoneRecent = Guid.NewGuid();
        var idemInProgressOld = Guid.NewGuid();
        var tokenDead = Guid.NewGuid();
        var tokenLive = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

            db.Users.Add(new User
            {
                Id = userId,
                Email = $"ret-{userId:N}@test.local",
                NormalizedEmail = $"RET-{userId:N}@TEST.LOCAL",
                PasswordHash = "x",
                Role = UserRole.Customer,
                CreatedAt = old
            });

            db.OutboxMessages.AddRange(
                new OutboxMessage { Id = outboxOld, Type = "RetTest", Payload = "{}", OccurredAt = old, ProcessedAt = old },
                new OutboxMessage { Id = outboxRecent, Type = "RetTest", Payload = "{}", OccurredAt = now, ProcessedAt = now },
                new OutboxMessage { Id = outboxUnprocessed, Type = "RetTest", Payload = "{}", OccurredAt = old, ProcessedAt = null });

            db.ProcessedMessages.AddRange(
                new ProcessedMessage { MessageId = dedupeOld, Consumer = "ret-test", ProcessedAt = old },
                new ProcessedMessage { MessageId = dedupeRecent, Consumer = "ret-test", ProcessedAt = now });

            var doneOld = NewIdempotency(idemDoneOld, tenantId, old);
            doneOld.Complete(old);
            var doneRecent = NewIdempotency(idemDoneRecent, tenantId, now);
            doneRecent.Complete(now);
            var inProgressOld = NewIdempotency(idemInProgressOld, tenantId, old); // never completed => keep
            db.IdempotencyRecords.AddRange(doneOld, doneRecent, inProgressOld);

            db.RefreshTokens.AddRange(
                new RefreshToken { Id = tokenDead, UserId = userId, FamilyId = Guid.NewGuid(), TokenHash = $"dead-{tokenDead:N}", CreatedAt = old, ExpiresAt = old },
                new RefreshToken { Id = tokenLive, UserId = userId, FamilyId = Guid.NewGuid(), TokenHash = $"live-{tokenLive:N}", CreatedAt = now, ExpiresAt = now.AddDays(7) });

            await db.SaveChangesAsync();
        }

        var service = new RetentionService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new RetentionOptions(),
            TimeProvider.System,
            NullLogger<RetentionService>.Instance);

        var result = await service.SweepAsync(CancellationToken.None);

        Assert.True(result.ProcessedOutbox >= 1);
        Assert.True(result.Dedupe >= 1);
        Assert.True(result.CompletedIdempotency >= 1);
        Assert.True(result.DeadRefreshTokens >= 1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

            // Old, dead rows are gone.
            Assert.False(await db.OutboxMessages.AnyAsync(o => o.Id == outboxOld));
            Assert.False(await db.ProcessedMessages.AnyAsync(p => p.MessageId == dedupeOld));
            Assert.False(await db.IdempotencyRecords.IgnoreQueryFilters().AnyAsync(i => i.Id == idemDoneOld));
            Assert.False(await db.RefreshTokens.AnyAsync(t => t.Id == tokenDead));

            // Live rows survive.
            Assert.True(await db.OutboxMessages.AnyAsync(o => o.Id == outboxRecent));
            Assert.True(await db.OutboxMessages.AnyAsync(o => o.Id == outboxUnprocessed)); // never delivered => keep
            Assert.True(await db.ProcessedMessages.AnyAsync(p => p.MessageId == dedupeRecent));
            Assert.True(await db.IdempotencyRecords.IgnoreQueryFilters().AnyAsync(i => i.Id == idemDoneRecent));
            Assert.True(await db.IdempotencyRecords.IgnoreQueryFilters().AnyAsync(i => i.Id == idemInProgressOld)); // not completed => keep
            Assert.True(await db.RefreshTokens.AnyAsync(t => t.Id == tokenLive));
        }
    }

    private static IdempotencyRecord NewIdempotency(Guid id, Guid tenantId, DateTimeOffset createdAt) => new()
    {
        Id = id,
        TenantId = tenantId,
        ActorKey = "retention-test",
        Key = $"key-{id:N}",
        RequestHash = "hash",
        OrderId = Guid.NewGuid(),
        CreatedAt = createdAt
    };
}
