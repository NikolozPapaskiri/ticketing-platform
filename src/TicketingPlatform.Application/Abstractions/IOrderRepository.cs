using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

public interface IOrderRepository
{
    /// <summary>Tracked hold with ticket type + inventory (pricing and the Confirm flow need the graph).</summary>
    Task<Hold?> GetHoldForOrderAsync(Guid holdId, CancellationToken ct);

    /// <summary>Customer checkout resolves the tenant from a hold before tenant filters can apply.</summary>
    Task<Guid?> GetHoldTenantIdAsync(Guid holdId, CancellationToken ct);

    Task<Order?> GetAsync(Guid orderId, CancellationToken ct);
    Task<IReadOnlyList<Order>> ListForCustomerAsync(Guid customerUserId, CancellationToken ct);
    Task<Order?> GetForCustomerAsync(Guid orderId, Guid customerUserId, CancellationToken ct);

    /// <summary>Tracked confirmed order graph for refunding and inventory release.</summary>
    Task<Order?> GetForRefundAsync(Guid orderId, CancellationToken ct);

    /// <summary>The issued ticket document record, if the async issuer has produced one yet.</summary>
    Task<Ticket?> GetTicketAsync(Guid orderId, CancellationToken ct);
    Task<Ticket?> GetTicketForCustomerAsync(Guid orderId, Guid customerUserId, CancellationToken ct);
    Task<Ticket?> GetTicketForUpdateAsync(Guid orderId, CancellationToken ct);
    Task<Ticket?> GetTicketByCodeForUpdateAsync(string code, CancellationToken ct);

    Task<IdempotencyRecord?> GetIdempotencyRecordAsync(
        Guid tenantId, string actorKey, string key, CancellationToken ct);

    void Add(Order order);
    void Add(IdempotencyRecord record);
    void Remove(IdempotencyRecord record);
    Task SaveChangesAsync(CancellationToken ct);
}
