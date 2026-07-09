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
    /// <summary>
    /// How long a hold reserves inventory before the expiry background service reclaims it.
    /// Constant for now; becomes per-tenant configuration when tenants get settings.
    /// </summary>
    public static readonly TimeSpan HoldTtl = TimeSpan.FromMinutes(10);

    private readonly IHoldRepository _holds;
    private readonly IReservationStrategy _reservation;
    private readonly ICacheService _cache;
    private readonly TimeProvider _clock;

    public HoldService(IHoldRepository holds, IReservationStrategy reservation, ICacheService cache, TimeProvider clock)
    {
        _holds = holds;
        _reservation = reservation;
        _cache = cache;
        _clock = clock; // injected clock keeps the TTL testable (FakeTimeProvider in tests)
    }

    public async Task<Result<HoldResponse>> CreateAsync(Guid tenantId, CreateHoldRequest request, CancellationToken ct)
    {
        // Tenant-scoped pre-check: a foreign tenant's ticket type is invisible => NotFound.
        // Also yields the owning EventId for cache invalidation after a successful reserve.
        var inventory = await _holds.GetInventoryForUpdateAsync(request.TicketTypeId, ct);
        if (inventory is null)
            return Result<HoldResponse>.NotFound($"Ticket type '{request.TicketTypeId}' was not found.");

        var now = _clock.GetUtcNow();
        var hold = new Hold
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TicketTypeId = request.TicketTypeId,
            Quantity = request.Quantity,
            CreatedAt = now,
            ExpiresAt = now.Add(HoldTtl)
        };

        // The contention-safe part. Insufficient stock (or a lost race) comes back as Conflict.
        var reserved = await _reservation.ReserveAsync(hold, ct);
        if (!reserved.IsSuccess)
        {
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

    public async Task<Result> ReleaseAsync(Guid holdId, CancellationToken ct)
    {
        var hold = await _holds.GetWithInventoryForUpdateAsync(holdId, ct);
        if (hold is null)
            return Result.NotFound($"Hold '{holdId}' was not found.");

        // Releasing a non-active hold is a state conflict (same reasoning as event transitions).
        if (!hold.CanRelease)
            return Result.Conflict($"Hold is already '{hold.Status}' and cannot be released.");

        hold.Release();
        await _reservation.ReleaseAsync(hold, ct); // credits inventory + persists the status flip

        await _cache.RemoveAsync(CacheKeys.EventGraph(hold.TenantId, hold.TicketType.EventId), ct);

        return Result.Success();
    }

    private static HoldResponse Map(Hold hold) =>
        new(hold.Id, hold.TicketTypeId, hold.Quantity, hold.Status.ToString(), hold.ExpiresAt);
}
