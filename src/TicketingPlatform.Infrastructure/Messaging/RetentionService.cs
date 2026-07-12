using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>Rows a single sweep removed, per table.</summary>
public readonly record struct RetentionResult(int ProcessedOutbox, int Dedupe, int CompletedIdempotency, int DeadRefreshTokens);

/// <summary>
/// Prunes the operational-bookkeeping tables that otherwise grow forever: delivered outbox rows,
/// per-consumer dedupe marks, completed idempotency records, and dead (expired/revoked) refresh
/// tokens. Worker-only and set-based (one <c>ExecuteDelete</c> per table), and it runs on a
/// background scope so it must ignore the tenant query filter - otherwise it would "succeed" while
/// deleting nothing. Multi-replica-safe: deletes are idempotent, so two sweeps racing just delete
/// the same already-gone rows.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RetentionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(IServiceScopeFactory scopes, RetentionOptions options, TimeProvider clock, ILogger<RetentionService> logger)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.SweepIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention sweep failed; retrying next interval");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Runs one sweep and returns what it removed. Public so it can be driven deterministically in tests.</summary>
    public async Task<RetentionResult> SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var now = _clock.GetUtcNow();

        var outbox = await db.OutboxMessages.IgnoreQueryFilters()
            .Where(o => o.ProcessedAt != null && o.ProcessedAt < now.AddDays(-_options.ProcessedOutboxRetentionDays))
            .ExecuteDeleteAsync(ct);

        var dedupe = await db.ProcessedMessages.IgnoreQueryFilters()
            .Where(p => p.ProcessedAt < now.AddDays(-_options.DedupeRetentionDays))
            .ExecuteDeleteAsync(ct);

        var idempotency = await db.IdempotencyRecords.IgnoreQueryFilters()
            .Where(i => i.Status == IdempotencyRecordStatus.Completed
                        && i.CompletedAt != null
                        && i.CompletedAt < now.AddDays(-_options.CompletedIdempotencyRetentionDays))
            .ExecuteDeleteAsync(ct);

        var tokenCutoff = now.AddDays(-_options.DeadRefreshTokenRetentionDays);
        var tokens = await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.ExpiresAt < tokenCutoff || (t.RevokedAt != null && t.RevokedAt < tokenCutoff))
            .ExecuteDeleteAsync(ct);

        Record("outbox", outbox);
        Record("dedupe", dedupe);
        Record("idempotency", idempotency);
        Record("refresh_token", tokens);

        if (outbox + dedupe + idempotency + tokens > 0)
            _logger.LogInformation(
                "Retention pruned {Outbox} outbox, {Dedupe} dedupe, {Idempotency} idempotency, {Tokens} refresh tokens",
                outbox, dedupe, idempotency, tokens);

        return new RetentionResult(outbox, dedupe, idempotency, tokens);
    }

    private static void Record(string table, int rows)
    {
        if (rows > 0)
            TicketingMetrics.RetentionRowsPruned.Add(rows, new KeyValuePair<string, object?>("table", table));
    }
}
