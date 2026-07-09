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
        _db.Inventories.FirstOrDefaultAsync(i => i.TicketTypeId == ticketTypeId, ct);

    public Task<Hold?> GetWithInventoryForUpdateAsync(Guid holdId, CancellationToken ct) =>
        _db.Holds
            .Include(h => h.TicketType)
                .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(h => h.Id == holdId, ct);

    public Task<Hold?> GetAsync(Guid holdId, CancellationToken ct) =>
        _db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct);

    public void Add(Hold hold) => _db.Holds.Add(hold);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
