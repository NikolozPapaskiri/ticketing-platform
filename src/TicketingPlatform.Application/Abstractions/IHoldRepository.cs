using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for hold persistence. All reads are tenant-scoped by the EF global query filter in the
/// implementation. The "ForUpdate" methods return tracked entities: the reserve/release flows
/// mutate inventory and the hold in one SaveChanges (one transaction).
/// </summary>
public interface IHoldRepository
{
    /// <summary>Tracked inventory for a ticket type; null when the ticket type is invisible to this tenant.</summary>
    Task<Inventory?> GetInventoryForUpdateAsync(Guid ticketTypeId, CancellationToken ct);

    /// <summary>
    /// Public/customer reservation path: resolves tenant and sale status before a tenant-scoped
    /// reservation can run. Uses IgnoreQueryFilters in Infrastructure.
    /// </summary>
    Task<TicketTypeSaleContext?> GetTicketTypeSaleContextAsync(Guid ticketTypeId, CancellationToken ct);

    Task<Guid?> GetHoldTenantIdAsync(Guid holdId, CancellationToken ct);

    /// <summary>Tracked hold including its ticket type's inventory (needed to give quantity back).</summary>
    Task<Hold?> GetWithInventoryForUpdateAsync(Guid holdId, CancellationToken ct);

    /// <summary>Read-only lookup.</summary>
    Task<Hold?> GetAsync(Guid holdId, CancellationToken ct);
    Task<IReadOnlyList<Hold>> ListForCustomerAsync(Guid customerUserId, CancellationToken ct);
    Task<Hold?> GetForCustomerAsync(Guid holdId, Guid customerUserId, CancellationToken ct);

    void Add(Hold hold);
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record TicketTypeSaleContext(Guid TenantId, Guid EventId, string EventStatus);
