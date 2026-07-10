using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

public sealed class OrderRepository : IOrderRepository
{
    private readonly TicketingDbContext _db;
    public OrderRepository(TicketingDbContext db) => _db = db;

    public Task<Hold?> GetHoldForOrderAsync(Guid holdId, CancellationToken ct) =>
        // Tracked graph: the saga confirms the hold and needs the ticket type for pricing.
        _db.Holds
            .Include(h => h.TicketType)
                .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(h => h.Id == holdId, ct);

    public Task<Guid?> GetHoldTenantIdAsync(Guid holdId, CancellationToken ct) =>
        _db.Holds
            .IgnoreQueryFilters()
            .Where(h => h.Id == holdId)
            .Select(h => (Guid?)h.TenantId)
            .FirstOrDefaultAsync(ct);

    public Task<Order?> GetAsync(Guid orderId, CancellationToken ct) =>
        _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public async Task<IReadOnlyList<Order>> ListForCustomerAsync(Guid customerUserId, CancellationToken ct) =>
        await _db.Orders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.CustomerUserId == customerUserId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);

    public Task<Order?> GetForCustomerAsync(Guid orderId, Guid customerUserId, CancellationToken ct) =>
        _db.Orders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerUserId == customerUserId, ct);

    public Task<Order?> GetForRefundAsync(Guid orderId, CancellationToken ct) =>
        _db.Orders
            .Include(o => o.Hold)
                .ThenInclude(h => h.TicketType)
                    .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public Task<Order?> GetOrderWithHoldForUpdateAsync(Guid orderId, CancellationToken ct) =>
        // Tracked graph, cross-tenant: the finalize/reconcile path runs before a tenant is set
        // and mutates order + hold (concurrency-token guarded) and possibly inventory.
        _db.Orders
            .IgnoreQueryFilters()
            .Include(o => o.Hold)
                .ThenInclude(h => h.TicketType)
                    .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public async Task<IReadOnlyList<Guid>> GetOrderIdsWithExpiredPaymentLeaseAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct) =>
        await _db.Orders
            .IgnoreQueryFilters()
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
        await _db.Orders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.RefundPending
                        && o.RefundClaimedAt != null
                        && o.RefundClaimedAt <= staleBefore)
            .OrderBy(o => o.RefundClaimedAt)
            .Take(batchSize)
            .Select(o => o.Id)
            .ToListAsync(ct);

    public Task<Ticket?> GetTicketAsync(Guid orderId, CancellationToken ct) =>
        _db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.OrderId == orderId, ct);

    public Task<Ticket?> GetTicketForCustomerAsync(Guid orderId, Guid customerUserId, CancellationToken ct) =>
        _db.Tickets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.OrderId == orderId)
            .Join(_db.Orders.IgnoreQueryFilters().Where(o => o.CustomerUserId == customerUserId),
                t => t.OrderId,
                o => o.Id,
                (t, _) => t)
            .FirstOrDefaultAsync(ct);

    public Task<Ticket?> GetTicketForUpdateAsync(Guid orderId, CancellationToken ct) =>
        // Cross-tenant: the order id is globally unique, and the reconciler (no tenant) must be
        // able to void the ticket when it settles a stranded refund.
        _db.Tickets.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.OrderId == orderId, ct);

    public Task<Ticket?> GetTicketByCodeForUpdateAsync(string code, CancellationToken ct) =>
        _db.Tickets.FirstOrDefaultAsync(t => t.Code == code, ct);

    public Task<IdempotencyRecord?> GetIdempotencyRecordAsync(
        Guid tenantId, string actorKey, string key, CancellationToken ct) =>
        _db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.ActorKey == actorKey && r.Key == key, ct);

    public Task<IdempotencyRecord?> GetIdempotencyForOrderForUpdateAsync(Guid orderId, CancellationToken ct) =>
        _db.IdempotencyRecords
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.OrderId == orderId, ct);

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
