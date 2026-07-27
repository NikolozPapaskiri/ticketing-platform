using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Persistence.Scopes;

// The five access scopes, deliberately in ONE file: this is the only place in the solution allowed
// to call IgnoreQueryFilters(), and an architecture test fails the suite if that changes.
//
// Ticketing is B2B2C, so "apply the tenant filter, always" is not a rule the product can keep - a
// third of its read paths legitimately span tenants. Rather than one filter plus ad-hoc escape
// hatches (where isolation then depends on whatever predicate the author remembered to write),
// every query answers one question: which plane am I in?
//
//   TenantScope     organizer plane. Filter ON, fails CLOSED on a missing tenant.
//   CustomerScope   customer plane. Cross-tenant, default-denied by OWNERSHIP.
//   PublicScope     marketplace. Cross-tenant, restricted to published state.
//   PlatformScope   platform admin. Cross-tenant, privileged, an authenticated human.
//   SystemScope     background workers. Tenant-less and privileged, no principal.
//
// See docs/MULTI_TENANCY.md §2.2.

/// <summary>
/// Organizer plane: staff, box office, scanners. Every entity here carries <c>TenantId</c> and the
/// EF global query filter does the work, so this scope adds no predicate of its own - it exists to
/// (a) give tenant-plane code a name, and (b) FAIL CLOSED.
///
/// Failing closed matters: with no tenant the filter compares <c>TenantId == null</c>, matches
/// nothing, and returns an empty result that looks exactly like "no data" - the worst possible
/// failure mode, because it is silent. Throwing turns a silent wrong answer into a loud bug.
/// </summary>
public sealed class TenantScope
{
    private readonly TicketingDbContext _db;
    private readonly ITenantContext _tenant;

    public TenantScope(TicketingDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>The tenant-filtered set. Throws when no tenant is established.</summary>
    public IQueryable<T> Of<T>() where T : class
    {
        if (_tenant.TenantId is null)
            throw new InvalidOperationException(
                $"TenantScope query for '{typeof(T).Name}' with no tenant established. The tenant " +
                "plane requires a tenant claim (staff) or an explicitly resolved tenant (customer " +
                "plane). Use SystemScope for tenant-less background work.");

        return _db.Set<T>();
    }
}

/// <summary>
/// Customer plane: ticket buyers. A customer belongs to NO tenant and transacts with many, so the
/// tenant filter is not the isolation mechanism here - OWNERSHIP is. Every query is rooted at the
/// authenticated customer, applied here rather than remembered method-by-method.
///
/// The tenant filter is bypassed on purpose: "my orders" spans every organizer I ever bought from.
/// </summary>
public sealed class CustomerScope
{
    private readonly TicketingDbContext _db;
    public CustomerScope(TicketingDbContext db) => _db = db;

    public IQueryable<Order> Orders(Guid customerUserId) =>
        _db.Orders.IgnoreQueryFilters().Where(o => o.CustomerUserId == customerUserId);

    public IQueryable<Hold> Holds(Guid customerUserId) =>
        _db.Holds.IgnoreQueryFilters().Where(h => h.CustomerUserId == customerUserId);

    /// <summary>
    /// Tickets are owned TRANSITIVELY, through the order that bought them - a Ticket has no
    /// CustomerUserId of its own. Expressing that as EXISTS keeps the ownership predicate in this
    /// one place and makes duplicate rows impossible by construction.
    /// </summary>
    public IQueryable<Ticket> Tickets(Guid customerUserId) =>
        _db.Tickets.IgnoreQueryFilters()
            .Where(t => _db.Orders.IgnoreQueryFilters()
                .Any(o => o.Id == t.OrderId && o.CustomerUserId == customerUserId));
}

/// <summary>
/// Marketplace plane: anonymous browse. Cross-tenant by definition - a global catalog across every
/// organizer IS the product - so the tenant filter cannot apply. What replaces it is PUBLISHED
/// STATE: a query here can only see events that are on sale.
/// </summary>
public sealed class PublicScope
{
    private readonly TicketingDbContext _db;
    public PublicScope(TicketingDbContext db) => _db = db;

    /// <summary>Publicly visible events. The OnSale predicate is baked in so it cannot be forgotten.</summary>
    public IQueryable<Event> OnSaleEvents() =>
        _db.Events.IgnoreQueryFilters().Where(e => e.Status == EventStatus.OnSale);

    /// <summary>
    /// Events by id REGARDLESS of published state. Deliberately named to be uncomfortable: it is
    /// the one public read that can observe a draft event, and it exists only to preserve current
    /// behaviour (see docs/MULTI_TENANCY.md - the image-path finding). Two callers: the event-image
    /// endpoint and the waiting-room state probe, which returns an is-on-sale flag rather than data.
    /// Do not add callers; restrict these two instead, as a deliberate behaviour change.
    /// </summary>
    public IQueryable<Event> EventsIncludingUnpublished() =>
        _db.Events.IgnoreQueryFilters();
}

/// <summary>
/// Platform-admin plane: cross-tenant reads by an authenticated PlatformAdmin (the ops snapshot).
/// Mechanically identical to <see cref="SystemScope"/>, deliberately separate because the
/// authorization story differs - there is a real principal here, so access is attributable and
/// auditable. Collapsing the two would lose that distinction at exactly the point it matters.
/// </summary>
public sealed class PlatformScope
{
    private readonly TicketingDbContext _db;
    public PlatformScope(TicketingDbContext db) => _db = db;

    /// <summary>Cross-tenant read for platform administration. Reads only - never mutate here.</summary>
    public IQueryable<T> Of<T>() where T : class => _db.Set<T>().IgnoreQueryFilters();
}

/// <summary>
/// System plane: background workers. A hosted service runs in a scope with NO tenant and no
/// principal, so it must bypass the filter - and it is the only plane that is privileged by
/// construction rather than by authorization.
/// </summary>
public sealed class SystemScope
{
    private readonly TicketingDbContext _db;
    public SystemScope(TicketingDbContext db) => _db = db;

    /// <summary>Cross-tenant, unfiltered. The workers' view of the world.</summary>
    public IQueryable<T> Of<T>() where T : class => _db.Set<T>().IgnoreQueryFilters();

    /// <summary>
    /// The AUTHORIZED WRITE CORE. Checkout finalize and refund funnel here from all three planes -
    /// customer (CustomerOrdersController), organizer (OrdersController), and the reconciler - after
    /// authorization has already happened upstream. On the first two a tenant IS established by the
    /// time this runs, so the filter would have sufficed; the bypass exists SOLELY for the
    /// reconciler, which settles stranded payments with no tenant at all.
    ///
    /// Callers must have authorized the caller already. This name is the reminder.
    /// </summary>
    public IQueryable<T> AuthorizedWriteCore<T>() where T : class => _db.Set<T>().IgnoreQueryFilters();
}
