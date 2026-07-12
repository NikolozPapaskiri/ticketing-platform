namespace TicketingPlatform.Application.Common;

/// <summary>
/// Retention windows for the tables that otherwise grow without bound (section <c>Retention</c>).
/// These rows are operational bookkeeping - once they are old enough that no redelivery, retry, or
/// reconciliation could still reference them, they are safe to prune.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>How often the sweep runs.</summary>
    public int SweepIntervalMinutes { get; init; } = 60;

    /// <summary>Delivered outbox rows older than this are pruned (kept briefly for post-hoc tracing).</summary>
    public int ProcessedOutboxRetentionDays { get; init; } = 7;

    /// <summary>Per-consumer dedupe marks older than this are pruned - well past any redelivery window.</summary>
    public int DedupeRetentionDays { get; init; } = 7;

    /// <summary>Completed API idempotency records older than this are pruned (the client is long done retrying).</summary>
    public int CompletedIdempotencyRetentionDays { get; init; } = 7;

    /// <summary>Refresh tokens expired or revoked longer ago than this are pruned - they can never be used again.</summary>
    public int DeadRefreshTokenRetentionDays { get; init; } = 3;
}
