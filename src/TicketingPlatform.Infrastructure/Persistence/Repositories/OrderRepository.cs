using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence.Scopes;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

/// <summary>
/// Orders span three planes, which is why this port looked so leaky before: the organizer reads its
/// own orders, the customer reads theirs across every organizer, and the reconciler settles
/// stranded payments with no tenant at all. Each method now names the plane it belongs to instead
/// of reaching for IgnoreQueryFilters and hand-writing a predicate.
/// </summary>
public sealed class OrderRepository : IOrderRepository
{
    private readonly TicketingDbContext _db;
    private readonly TenantScope _tenant;
    private readonly CustomerScope _customer;
    private readonly SystemScope _system;

    public OrderRepository(TicketingDbContext db, TenantScope tenant, CustomerScope customer, SystemScope system)
    {
        _db = db;
        _tenant = tenant;
        _customer = customer;
        _system = system;
    }

    // --- Organizer plane -----------------------------------------------------------------------

    public Task<Hold?> GetHoldForOrderAsync(Guid holdId, CancellationToken ct) =>
        // Tracked graph: the saga confirms the hold and needs the ticket type for pricing.
        _tenant.Of<Hold>()
            .Include(h => h.TicketType)
                .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(h => h.Id == holdId, ct);

    public Task<Order?> GetAsync(Guid orderId, CancellationToken ct) =>
        _tenant.Of<Order>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public Task<Order?> GetForRefundAsync(Guid orderId, CancellationToken ct) =>
        _tenant.Of<Order>()
            .Include(o => o.Hold)
                .ThenInclude(h => h.TicketType)
                    .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public Task<Ticket?> GetTicketAsync(Guid orderId, CancellationToken ct) =>
        _tenant.Of<Ticket>().AsNoTracking().FirstOrDefaultAsync(t => t.OrderId == orderId, ct);

    public Task<Ticket?> GetTicketByCodeForUpdateAsync(string code, CancellationToken ct) =>
        _tenant.Of<Ticket>().FirstOrDefaultAsync(t => t.Code == code, ct);

    public Task<IdempotencyRecord?> GetIdempotencyRecordAsync(
        Guid tenantId, string actorKey, string key, CancellationToken ct) =>
        _tenant.Of<IdempotencyRecord>()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.ActorKey == actorKey && r.Key == key, ct);

    // --- Customer plane ------------------------------------------------------------------------
    // Cross-tenant on purpose ("my orders" spans every organizer). The ownership predicate lives in
    // CustomerScope, so it cannot be forgotten here.

    public async Task<IReadOnlyList<Order>> ListForCustomerAsync(Guid customerUserId, CancellationToken ct) =>
        await _customer.Orders(customerUserId)
            .AsNoTracking()
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);

    public Task<Order?> GetForCustomerAsync(Guid orderId, Guid customerUserId, CancellationToken ct) =>
        _customer.Orders(customerUserId)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public Task<Ticket?> GetTicketForCustomerAsync(Guid orderId, Guid customerUserId, CancellationToken ct) =>
        // Ticket ownership is transitive through the order; CustomerScope expresses that join once.
        _customer.Tickets(customerUserId)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.OrderId == orderId, ct);

    // --- System plane --------------------------------------------------------------------------

    public Task<Guid?> GetHoldTenantIdAsync(Guid holdId, CancellationToken ct) =>
        // Bootstrap only: the customer controllers need the owning tenant before they can open a
        // tenant scope. Projects a tenant id, never an entity.
        _system.TenantDiscovery<Hold>()
            .Where(h => h.Id == holdId)
            .Select(h => (Guid?)h.TenantId)
            .FirstOrDefaultAsync(ct);

    public Task<Order?> GetOrderWithHoldForUpdateAsync(Guid orderId, CancellationToken ct) =>
        // Tracked graph. Entered from all three planes after authorization; the filter bypass is
        // needed only by the reconciler, which runs with no tenant.
        _system.AuthorizedWriteCore<Order>()
            .Include(o => o.Hold)
                .ThenInclude(h => h.TicketType)
                    .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public Task<Ticket?> GetTicketForUpdateAsync(Guid orderId, CancellationToken ct) =>
        // The reconciler (no tenant) must be able to void the ticket when it settles a refund.
        _system.AuthorizedWriteCore<Ticket>().FirstOrDefaultAsync(t => t.OrderId == orderId, ct);

    public Task<IdempotencyRecord?> GetIdempotencyForOrderForUpdateAsync(Guid orderId, CancellationToken ct) =>
        _system.AuthorizedWriteCore<IdempotencyRecord>().FirstOrDefaultAsync(r => r.OrderId == orderId, ct);

    public async Task<IReadOnlyList<Guid>> GetOrderIdsWithExpiredPaymentLeaseAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct) =>
        await _system.Of<Order>()
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.PendingPayment
                        && o.Hold.Status == HoldStatus.PaymentPending
                        && o.Hold.PaymentLeaseUntil != null
                        && o.Hold.PaymentLeaseUntil <= now)
            .OrderBy(o => o.Hold.PaymentLeaseUntil)
            .Take(batchSize)
            .Select(o => o.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetOrderIdsWithStaleRefundClaimAsync(
        DateTimeOffset staleBefore, int batchSize, CancellationToken ct) =>
        await _system.Of<Order>()
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.RefundPending
                        && o.RefundClaimedAt != null
                        && o.RefundClaimedAt <= staleBefore)
            .OrderBy(o => o.RefundClaimedAt)
            .Take(batchSize)
            .Select(o => o.Id)
            .ToListAsync(ct);

    // --- Writes --------------------------------------------------------------------------------

    public void Add(Order order) => _db.Orders.Add(order);

    public void Add(IdempotencyRecord record) => _db.IdempotencyRecords.Add(record);

    public void Remove(IdempotencyRecord record) => _db.IdempotencyRecords.Remove(record);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    public async Task<SaveOutcome> TrySaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return SaveOutcome.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear(); // abandon the failed changes so the caller can re-read cleanly
            return SaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.ChangeTracker.Clear();
            return SaveOutcome.UniqueViolation;
        }
    }
}
