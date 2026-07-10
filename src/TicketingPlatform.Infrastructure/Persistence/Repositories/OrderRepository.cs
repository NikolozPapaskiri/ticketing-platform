using Microsoft.EntityFrameworkCore;
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
        _db.Tickets.FirstOrDefaultAsync(t => t.OrderId == orderId, ct);

    public Task<Ticket?> GetTicketByCodeForUpdateAsync(string code, CancellationToken ct) =>
        _db.Tickets.FirstOrDefaultAsync(t => t.Code == code, ct);

    public Task<IdempotencyRecord?> GetIdempotencyRecordAsync(
        Guid tenantId, string actorKey, string key, CancellationToken ct) =>
        _db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.ActorKey == actorKey && r.Key == key, ct);

    public void Add(Order order) => _db.Orders.Add(order);

    public void Add(IdempotencyRecord record) => _db.IdempotencyRecords.Add(record);

    public void Remove(IdempotencyRecord record) => _db.IdempotencyRecords.Remove(record);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
