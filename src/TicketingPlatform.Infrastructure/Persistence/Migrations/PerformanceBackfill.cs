namespace TicketingPlatform.Infrastructure.Persistence.Migrations;

/// <summary>
/// The Phase A slice 3 data backfill, kept as constants so the migration and its test execute the
/// EXACT same statements. A data migration that is only exercised the one time it runs in
/// production is the least-tested code in a codebase; this makes it assertable.
///
/// Every existing event becomes a one-night run: one performance carrying the event's own StartsAt,
/// and its ticket types point at that performance. Nothing reads the new column yet - this is the
/// expand step, so the shape exists and is populated while every query still goes through EventId.
/// </summary>
public static class PerformanceBackfill
{
    /// <summary>
    /// One performance per event that has none. Guarded by NOT EXISTS so re-running is harmless,
    /// and so an event that already has real dates is never given a synthetic one.
    /// </summary>
    public const string CreateOnePerformancePerEvent = """
        INSERT INTO "Performances"
            ("Id", "TenantId", "EventId", "HallId", "SeatMapVersionId",
             "StartsAt", "DoorsOpenAt", "Status", "CancelledAt", "CreatedAt")
        SELECT gen_random_uuid(), e."TenantId", e."Id", NULL, NULL,
               e."StartsAt", NULL, 'Scheduled', NULL, now()
        FROM "Events" e
        WHERE NOT EXISTS (SELECT 1 FROM "Performances" p WHERE p."EventId" = e."Id");
        """;

    /// <summary>
    /// Point legacy ticket types at their event's earliest performance. DISTINCT ON makes the choice
    /// deterministic rather than "whichever row the planner happened to join" - at backfill time
    /// there is exactly one per event, but the statement must not depend on that being true.
    /// </summary>
    public const string LinkTicketTypesToTheirPerformance = """
        UPDATE "TicketTypes" tt
        SET "PerformanceId" = chosen."Id"
        FROM (
            SELECT DISTINCT ON (p."EventId") p."EventId", p."Id"
            FROM "Performances" p
            ORDER BY p."EventId", p."StartsAt", p."Id"
        ) AS chosen
        WHERE chosen."EventId" = tt."EventId" AND tt."PerformanceId" IS NULL;
        """;
}
