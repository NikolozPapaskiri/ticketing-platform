using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

public sealed class TenantRepository : ITenantRepository
{
    private readonly TicketingDbContext _db;
    public TenantRepository(TicketingDbContext db) => _db = db;

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct) =>
        await _db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct) =>
        _db.Tenants.AnyAsync(t => t.Slug == slug, ct);

    public void Add(Tenant tenant) => _db.Tenants.Add(tenant);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
