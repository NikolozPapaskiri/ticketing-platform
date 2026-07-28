namespace TicketingPlatform.Infrastructure.Persistence.Migrations;

/// <summary>
/// The Phase A slice 3 data backfill: every existing event becomes a one-night run carrying its own
/// Events."StartsAt", and its ticket types point at that performance. Run twice - once by the expand
/// step, once by the contract step immediately before making the column NOT NULL, which is safe only
/// because the statements are idempotent by construction.
///
/// HISTORICAL. These target the PRE-CONTRACT schema: Events."StartsAt" no longer exists, so they can
/// only ever run inside the two migrations that precede the column being dropped. They are kept as
/// constants because those migrations must keep executing exactly this text on any database being
/// built up from scratch - do not "fix" them to match the current schema, and do not add callers.
///
/// They were extracted here so a test could execute the exact same statements a migration does - a
/// data migration exercised only the one time it runs in production is the least-tested code in a
/// codebase. Those tests retired with the column: once the old shape cannot be inserted, the legacy
/// scenario cannot be set up. That is the trade a contract step makes.
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
