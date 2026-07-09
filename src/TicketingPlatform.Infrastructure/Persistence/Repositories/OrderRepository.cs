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

    public Task<Order?> GetAsync(Guid orderId, CancellationToken ct) =>
        _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public Task<Ticket?> GetTicketAsync(Guid orderId, CancellationToken ct) =>
        _db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.OrderId == orderId, ct);

    public void Add(Order order) => _db.Orders.Add(order);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
