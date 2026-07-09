using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Api.Auth;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Api.Features.SalesReport;

/// <summary>
/// THE VERTICAL SLICE SET PIECE - read this file as an architecture argument, not just a feature.
///
/// Everything the "event sales report" feature needs lives in THIS ONE FILE: route, handler,
/// query, response shapes. Compare with the layered path every other feature takes (controller
/// -> Application service -> repository port -> EF implementation, four projects deep).
///
/// What the slice buys:
///  - Locality: one file to read, change, review, or delete. No cross-project navigation.
///  - Speed: no port, no service, no DTO mapping ceremony for a feature nothing else reuses.
///  - Honest coupling: a read-only report over EF-translated SQL IS coupled to EF; pretending
///    otherwise through a repository abstraction adds indirection, not protection.
///
/// What it costs (and why the rest of the codebase stays layered):
///  - It reaches TicketingDbContext directly from the Api project - the ONE deliberate breach
///    of "Api has zero EF" in this codebase. Fine for an isolated read; fatal if business
///    rules (the state machine, reservation logic) started living in slices, because rules
///    in endpoints cannot be unit-tested or reused, and every slice re-decides conventions.
///  - Slices do not compose: the moment two slices need the same rule, you extract it - and
///    you have reinvented the Application layer.
///
/// The senior answer is not "Clean vs Slice" but WHERE each: protected shared domain in
/// layers; leaf features nothing else depends on (reports, exports, admin screens) as slices.
/// Uses minimal APIs for contrast with the controllers, per the learning plan.
/// </summary>
public static class GetEventSalesReport
{
    public static IEndpointRouteBuilder MapEventSalesReport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/events/{id:guid}/sales-report", HandleAsync)
           .RequireAuthorization(AuthPolicies.OrganizerStaff);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        Guid id, TicketingDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        if (!tenant.HasTenant)
            return Results.Problem(title: "Missing tenant", statusCode: StatusCodes.Status400BadRequest);

        // Tenant-scoped by the global query filter, like every read in the system.
        var eventName = await db.Events.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => e.Name)
            .FirstOrDefaultAsync(ct);
        if (eventName is null)
            return Results.NotFound();

        // Project the scalar columns in SQL (translated to Orders JOIN Holds JOIN TicketTypes),
        // then group in memory. A grouped aggregate that reaches THROUGH navigations
        // (Sum(o => o.Hold.Quantity)) is a known EF translation gap; for a bounded per-event
        // report, materializing the confirmed lines and grouping with LINQ-to-Objects is both
        // correct and honest - the interview point is knowing WHERE the SQL/in-memory line sits.
        var confirmed = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Confirmed && o.Hold.TicketType.EventId == id)
            .Select(o => new { TicketTypeName = o.Hold.TicketType.Name, o.Hold.Quantity, o.Amount })
            .ToListAsync(ct);

        var lines = confirmed
            .GroupBy(o => o.TicketTypeName)
            .Select(g => new SalesReportLine(g.Key, g.Sum(o => o.Quantity), g.Sum(o => o.Amount)))
            .OrderBy(l => l.TicketTypeName)
            .ToList();

        return Results.Ok(new SalesReportResponse(
            id,
            eventName,
            lines.Sum(l => l.TicketsSold),
            lines.Sum(l => l.Revenue),
            lines));
    }
}

// Response shapes are slice-local on purpose: nothing else consumes them.
public sealed record SalesReportLine(string TicketTypeName, int TicketsSold, decimal Revenue);

public sealed record SalesReportResponse(
    Guid EventId,
    string EventName,
    int TotalTicketsSold,
    decimal TotalRevenue,
    IReadOnlyList<SalesReportLine> Lines);
