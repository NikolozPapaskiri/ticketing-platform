using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence.Scopes;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

public sealed class HoldRepository : IHoldRepository
{
    private readonly TicketingDbContext _db;
    private readonly TenantScope _tenant;
    private readonly CustomerScope _customer;
    private readonly SystemScope _system;

    public HoldRepository(TicketingDbContext db, TenantScope tenant, CustomerScope customer, SystemScope system)
    {
        _db = db;
        _tenant = tenant;
        _customer = customer;
        _system = system;
    }

    public Task<Inventory?> GetInventoryForUpdateAsync(Guid ticketTypeId, CancellationToken ct) =>
        // Tracked: the caller mutates AvailableQuantity and SaveChanges persists the diff.
        // The tenant query filter applies here too - foreign inventory resolves to null.
        // TicketType is included so the service knows the owning EventId (cache invalidation).
        _tenant.Of<Inventory>()
            .Include(i => i.TicketType)
            .FirstOrDefaultAsync(i => i.TicketTypeId == ticketTypeId, ct);

    public Task<TicketTypeSaleContext?> GetTicketTypeSaleContextAsync(Guid ticketTypeId, CancellationToken ct) =>
        _system.TenantDiscovery<TicketType>()
            .AsNoTracking()
            .Where(tt => tt.Id == ticketTypeId)
            .Select(tt => new TicketTypeSaleContext(tt.TenantId, tt.EventId, tt.Event.Status.ToString(), tt.Event.WaitingRoomEnabled))
            .FirstOrDefaultAsync(ct);

    public Task<Guid?> GetHoldTenantIdAsync(Guid holdId, CancellationToken ct) =>
        _system.TenantDiscovery<Hold>()
            .Where(h => h.Id == holdId)
            .Select(h => (Guid?)h.TenantId)
            .FirstOrDefaultAsync(ct);

    public Task<Hold?> GetWithInventoryForUpdateAsync(Guid holdId, CancellationToken ct) =>
        _tenant.Of<Hold>()
            .Include(h => h.TicketType)
                .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(h => h.Id == holdId, ct);

    public Task<Hold?> GetAsync(Guid holdId, CancellationToken ct) =>
        _tenant.Of<Hold>().AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct);

    public async Task<IReadOnlyList<Hold>> ListForCustomerAsync(Guid customerUserId, CancellationToken ct) =>
        await _customer.Holds(customerUserId)
            .AsNoTracking()
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

    public Task<Hold?> GetForCustomerAsync(Guid holdId, Guid customerUserId, CancellationToken ct) =>
        _customer.Holds(customerUserId)
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == holdId, ct);

    public void Add(Hold hold) => _db.Holds.Add(hold);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
