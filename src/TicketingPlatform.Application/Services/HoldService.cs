using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Services;

/// <summary>
/// Hold use cases: reserve inventory under a TTL, release it back. The actual contention-safe
/// decrement is delegated to IReservationStrategy - three implementations (optimistic xmin,
/// pessimistic FOR UPDATE, Redis atomic) selected by the "Reservation:Strategy" config value.
/// Each strategy persists the decrement and the hold row atomically.
/// </summary>
public sealed class HoldService
{
    private readonly IHoldRepository _holds;
    private readonly IReservationStrategy _reservation;
    private readonly ICacheService _cache;
    private readonly IOutbox _outbox;
    private readonly HoldOptions _options;
    private readonly TimeProvider _clock;

    public HoldService(IHoldRepository holds, IReservationStrategy reservation, ICacheService cache,
        IOutbox outbox, HoldOptions options, TimeProvider clock)
    {
        _holds = holds;
        _reservation = reservation;
        _cache = cache;
        _outbox = outbox;
        _options = options; // TTL from the "Holds" config section
        _clock = clock;     // injected clock keeps the TTL testable (FakeTimeProvider in tests)
    }

    public async Task<Result<HoldResponse>> CreateAsync(Guid tenantId, CreateHoldRequest request, CancellationToken ct)
        => await CreateAsync(tenantId, request, customerUserId: null, ct);

    public async Task<Result<HoldResponse>> CreateAsync(
        Guid tenantId, CreateHoldRequest request, Guid? customerUserId, CancellationToken ct)
    {
        TicketingMetrics.HoldAttempts.Add(1);

        // Tenant-scoped pre-check: a foreign tenant's ticket type is invisible => NotFound.
        // Also yields the owning EventId for cache invalidation after a successful reserve.
        var inventory = await _holds.GetInventoryForUpdateAsync(request.TicketTypeId, ct);
        if (inventory is null)
        {
            TicketingMetrics.HoldConflicts.Add(1, new KeyValuePair<string, object?>("reason", "not_found"));
            return Result<HoldResponse>.NotFound($"Ticket type '{request.TicketTypeId}' was not found.");
        }

        var now = _clock.GetUtcNow();
        var hold = new Hold
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TicketTypeId = request.TicketTypeId,
            CustomerUserId = customerUserId,
            Quantity = request.Quantity,
            CreatedAt = now,
            ExpiresAt = now.Add(_options.Ttl)
        };

        // Staged BEFORE the strategy runs: the strategy's SaveChanges flushes this outbox row
        // together with the decrement + hold row (same scoped DbContext = same transaction).
        // On a Conflict the strategy never saves, so the staged row dies with the request scope.
        // The projection consumer re-reads live availability, so the event only needs ids.
        _outbox.Add("AvailabilityChanged", new
        {
            TenantId = tenantId,
            inventory.TicketType.EventId,
            TicketTypeId = request.TicketTypeId
        });

        // The contention-safe part. Insufficient stock (or a lost race) comes back as Conflict.
        var reserved = await _reservation.ReserveAsync(hold, ct);
        if (!reserved.IsSuccess)
        {
            TicketingMetrics.HoldConflicts.Add(1, new KeyValuePair<string, object?>("reason", reserved.Error.ToString()));
            return reserved.Error == ResultError.NotFound
                ? Result<HoldResponse>.NotFound(reserved.Message!)
                : Result<HoldResponse>.Conflict(reserved.Message!);
        }

        // Read-your-writes: staff who just reserved must see the new availability immediately.
        await _cache.RemoveAsync(CacheKeys.EventGraph(tenantId, inventory.TicketType.EventId), ct);

        return Result<HoldResponse>.Success(Map(hold));
    }

    public async Task<Result<HoldResponse>> GetByIdAsync(Guid holdId, CancellationToken ct)
    {
        var hold = await _holds.GetAsync(holdId, ct);
        return hold is null
            ? Result<HoldResponse>.NotFound($"Hold '{holdId}' was not found.")
            : Result<HoldResponse>.Success(Map(hold));
    }

    public async Task<IReadOnlyList<HoldResponse>> ListForCustomerAsync(Guid customerUserId, CancellationToken ct)
    {
        var holds = await _holds.ListForCustomerAsync(customerUserId, ct);
        return holds.Select(Map).ToList();
    }

    public async Task<Result<HoldResponse>> GetForCustomerAsync(Guid holdId, Guid customerUserId, CancellationToken ct)
    {
        var hold = await _holds.GetForCustomerAsync(holdId, customerUserId, ct);
        return hold is null
            ? Result<HoldResponse>.NotFound($"Hold '{holdId}' was not found.")
            : Result<HoldResponse>.Success(Map(hold));
    }

    public async Task<Result> ReleaseAsync(Guid holdId, CancellationToken ct)
    {
        var hold = await _holds.GetWithInventoryForUpdateAsync(holdId, ct);
        if (hold is null)
            return Result.NotFound($"Hold '{holdId}' was not found.");

        // Releasing a non-active hold is a state conflict (same reasoning as event transitions).
        if (!hold.CanRelease)
            return Result.Conflict($"Hold is already '{hold.Status}' and cannot be released.");

        hold.Release();

        // Same pattern as CreateAsync: staged before the strategy's save commits everything.
        _outbox.Add("AvailabilityChanged", new
        {
            hold.TenantId,
            hold.TicketType.EventId,
            hold.TicketTypeId
        });

        await _reservation.ReleaseAsync(hold, ct); // credits inventory + persists the status flip

        await _cache.RemoveAsync(CacheKeys.EventGraph(hold.TenantId, hold.TicketType.EventId), ct);

        return Result.Success();
    }

    private static HoldResponse Map(Hold hold) =>
        new(hold.Id, hold.TicketTypeId, hold.Quantity, hold.Status.ToString(), hold.ExpiresAt);
}
