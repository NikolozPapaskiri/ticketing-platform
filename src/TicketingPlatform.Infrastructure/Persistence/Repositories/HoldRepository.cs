using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

public sealed class HoldRepository : IHoldRepository
{
    private readonly TicketingDbContext _db;
    public HoldRepository(TicketingDbContext db) => _db = db;

    public Task<Inventory?> GetInventoryForUpdateAsync(Guid ticketTypeId, CancellationToken ct) =>
        // Tracked: the caller mutates AvailableQuantity and SaveChanges persists the diff.
        // The tenant query filter applies here too - foreign inventory resolves to null.
        // TicketType is included so the service knows the owning EventId (cache invalidation).
        _db.Inventories
            .Include(i => i.TicketType)
            .FirstOrDefaultAsync(i => i.TicketTypeId == ticketTypeId, ct);

    public Task<TicketTypeSaleContext?> GetTicketTypeSaleContextAsync(Guid ticketTypeId, CancellationToken ct) =>
        _db.TicketTypes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(tt => tt.Id == ticketTypeId)
            .Select(tt => new TicketTypeSaleContext(tt.TenantId, tt.EventId, tt.Event.Status.ToString(), tt.Event.WaitingRoomEnabled))
            .FirstOrDefaultAsync(ct);

    public Task<Guid?> GetHoldTenantIdAsync(Guid holdId, CancellationToken ct) =>
        _db.Holds
            .IgnoreQueryFilters()
            .Where(h => h.Id == holdId)
            .Select(h => (Guid?)h.TenantId)
            .FirstOrDefaultAsync(ct);

    public Task<Hold?> GetWithInventoryForUpdateAsync(Guid holdId, CancellationToken ct) =>
        _db.Holds
            .Include(h => h.TicketType)
                .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(h => h.Id == holdId, ct);

    public Task<Hold?> GetAsync(Guid holdId, CancellationToken ct) =>
        _db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct);

    public async Task<IReadOnlyList<Hold>> ListForCustomerAsync(Guid customerUserId, CancellationToken ct) =>
        await _db.Holds
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(h => h.CustomerUserId == customerUserId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

    public Task<Hold?> GetForCustomerAsync(Guid holdId, Guid customerUserId, CancellationToken ct) =>
        _db.Holds
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == holdId && h.CustomerUserId == customerUserId, ct);

    public void Add(Hold hold) => _db.Holds.Add(hold);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
