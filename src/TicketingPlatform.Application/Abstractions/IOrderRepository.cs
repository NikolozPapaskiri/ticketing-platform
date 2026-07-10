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

    /// <summary>
    /// Tracked order + hold + ticket type + inventory for the payment FINALIZE / reconcile step:
    /// the confirm/decline transitions run through change tracking (guarded by concurrency tokens)
    /// so exactly one finalizer wins, and inventory can be released on a proven no-charge.
    /// </summary>
    Task<Order?> GetOrderWithHoldForUpdateAsync(Guid orderId, CancellationToken ct);

    /// <summary>
    /// The reconciler's work list: ids of PendingPayment orders whose hold's payment lease has
    /// expired (an attempt that never finalized). Cross-tenant (a background scope has no tenant).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOrderIdsWithExpiredPaymentLeaseAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct);

    /// <summary>The issued ticket document record, if the async issuer has produced one yet.</summary>
    Task<Ticket?> GetTicketAsync(Guid orderId, CancellationToken ct);
    Task<Ticket?> GetTicketForCustomerAsync(Guid orderId, Guid customerUserId, CancellationToken ct);
    Task<Ticket?> GetTicketForUpdateAsync(Guid orderId, CancellationToken ct);
    Task<Ticket?> GetTicketByCodeForUpdateAsync(string code, CancellationToken ct);

    Task<IdempotencyRecord?> GetIdempotencyRecordAsync(
        Guid tenantId, string actorKey, string key, CancellationToken ct);

    /// <summary>Tracked idempotency record for a given order (to Complete it during finalize).</summary>
    Task<IdempotencyRecord?> GetIdempotencyForOrderForUpdateAsync(Guid orderId, CancellationToken ct);

    void Add(Order order);
    void Add(IdempotencyRecord record);
    void Remove(IdempotencyRecord record);
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Persist and classify the outcome so the Application layer can branch on a concurrency
    /// conflict (a lost claim / double finalize) or a unique violation (a same-key race) WITHOUT
    /// referencing EF or Npgsql exception types. On a conflict the change tracker is cleared so
    /// the caller can safely re-read.
    /// </summary>
    Task<SaveOutcome> TrySaveChangesAsync(CancellationToken ct);
}

public enum SaveOutcome
{
    Success,
    ConcurrencyConflict, // an optimistic-concurrency token no longer matched (someone else won)
    UniqueViolation      // a unique index rejected the insert (e.g. a duplicate idempotency key)
}
