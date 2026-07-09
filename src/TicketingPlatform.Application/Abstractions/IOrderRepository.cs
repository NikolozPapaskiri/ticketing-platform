using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

public interface IOrderRepository
{
    /// <summary>Tracked hold with ticket type + inventory (pricing and the Confirm flow need the graph).</summary>
    Task<Hold?> GetHoldForOrderAsync(Guid holdId, CancellationToken ct);

    Task<Order?> GetAsync(Guid orderId, CancellationToken ct);
    void Add(Order order);
    Task SaveChangesAsync(CancellationToken ct);
}
