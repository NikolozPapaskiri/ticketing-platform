using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for tenant persistence. Defined in Application, implemented in Infrastructure (EF Core).
/// Aggregate-shaped on purpose: use-case methods, no IQueryable leaks, so the EF surface stays
/// entirely inside Infrastructure.
/// </summary>
public interface ITenantRepository
{
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct);
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);
    void Add(Tenant tenant);
    Task SaveChangesAsync(CancellationToken ct);
}
