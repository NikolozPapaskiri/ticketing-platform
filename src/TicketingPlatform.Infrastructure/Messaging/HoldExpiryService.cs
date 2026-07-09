using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// The saga's compensation arm: Active holds past their TTL are expired and their quantity
/// flows back to inventory - a buyer who reserved and walked away (or whose payment kept
/// failing) cannot strand stock forever.
/// Two details worth internalizing:
///  - IgnoreQueryFilters: a background scope has NO tenant, so without it the tenant filter
///    would silently return zero rows and this service would look "healthy" while doing nothing.
///  - This naive per-instance timer runs N times with N replicas. Here that is SAFE because
///    Expire() guards the transition and the optimistic token guards the credit (a second
///    replica's attempt conflicts and skips). Jobs without such guards need a durable scheduler
///    (Hangfire/Quartz) or leader election - the Phase 6 scheduling discussion.
/// </summary>
public sealed class HoldExpiryService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly HoldOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<HoldExpiryService> _logger;

    public HoldExpiryService(IServiceScopeFactory scopes, HoldOptions options, TimeProvider clock, ILogger<HoldExpiryService> logger)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpireBatchAsync(stoppingToken);
                await Task.Delay(_options.ExpiryScanInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hold expiry scan failed; retrying next interval");
                try { await Task.Delay(_options.ExpiryScanInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ExpireBatchAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var now = _clock.GetUtcNow();

        var expired = await db.Holds
            .IgnoreQueryFilters() // background scope has no tenant - see class docs
            .Include(h => h.TicketType).ThenInclude(tt => tt.Inventory)
            .Where(h => h.Status == HoldStatus.Active && h.ExpiresAt <= now)
            .OrderBy(h => h.ExpiresAt)
            .Take(50)
            .ToListAsync(ct);

        if (expired.Count == 0)
            return;

        foreach (var hold in expired)
        {
            hold.Expire();
            hold.TicketType.Inventory.Release(hold.Quantity);

            // Announce through the outbox like every other state change.
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "HoldExpired",
                Payload = JsonSerializer.Serialize(new
                {
                    HoldId = hold.Id,
                    hold.TenantId,
                    hold.TicketTypeId,
                    hold.Quantity
                }, Json),
                OccurredAt = now
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);

            // Released inventory changes what the event graph shows - same read-your-writes
            // rule as the API's hold endpoints, so the cached graphs are invalidated too.
            var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();
            foreach (var hold in expired)
                await cache.RemoveAsync(CacheKeys.EventGraph(hold.TenantId, hold.TicketType.EventId), ct);

            _logger.LogInformation("Expired {Count} hold(s) and released their inventory", expired.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A live buyer (or another replica) won a race on one of these rows. Skip this
            // round; the next scan re-reads fresh state. Correctness never depends on this pass.
            _logger.LogWarning("Hold expiry batch conflicted with concurrent activity; will retry next scan");
        }
    }
}
