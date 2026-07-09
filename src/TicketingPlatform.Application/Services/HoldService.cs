using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Services;

/// <summary>
/// Hold use cases: reserve inventory under a TTL, release it back. The inventory decrement and
/// the hold row are saved in ONE SaveChanges, i.e. one database transaction - a crash between
/// them can never strand reserved quantity without a hold record.
/// Phase 2 scope: single-threaded correctness. Two concurrent buyers can still race the
/// TryReserve check; Phase 5 closes that with the xmin token (+ retry), pessimistic locking,
/// and a Redis atomic decrement, compared head to head.
/// </summary>
public sealed class HoldService
{
    /// <summary>
    /// How long a hold reserves inventory before the Phase 5 expiry service reclaims it.
    /// Constant for now; becomes per-tenant configuration when tenants get settings.
    /// </summary>
    public static readonly TimeSpan HoldTtl = TimeSpan.FromMinutes(10);

    private readonly IHoldRepository _holds;
    private readonly ICacheService _cache;
    private readonly TimeProvider _clock;

    public HoldService(IHoldRepository holds, ICacheService cache, TimeProvider clock)
    {
        _holds = holds;
        _cache = cache;
        _clock = clock; // injected clock keeps the TTL testable (FakeTimeProvider in tests)
    }

    public async Task<Result<HoldResponse>> CreateAsync(Guid tenantId, CreateHoldRequest request, CancellationToken ct)
    {
        // Tenant-scoped tracked load: a foreign tenant's ticket type is invisible => NotFound.
        var inventory = await _holds.GetInventoryForUpdateAsync(request.TicketTypeId, ct);
        if (inventory is null)
            return Result<HoldResponse>.NotFound($"Ticket type '{request.TicketTypeId}' was not found.");

        // Insufficient stock is an expected outcome, not an error: 409, with the current
        // availability so the client can offer the buyer what is actually left.
        if (!inventory.TryReserve(request.Quantity))
            return Result<HoldResponse>.Conflict(
                $"Cannot hold {request.Quantity} tickets; only {inventory.AvailableQuantity} available.");

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

        _holds.Add(hold);
        await _holds.SaveChangesAsync(ct); // decrement + hold row commit together

        // Read-your-writes: staff who just reserved must see the new availability immediately,
        // so the owning event's cached graph is invalidated (after the commit).
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
        hold.TicketType.Inventory.Release(hold.Quantity);
        await _holds.SaveChangesAsync(ct); // status flip + quantity return in one transaction

        await _cache.RemoveAsync(CacheKeys.EventGraph(hold.TenantId, hold.TicketType.EventId), ct);

        return Result.Success();
    }

    private static HoldResponse Map(Hold hold) =>
        new(hold.Id, hold.TicketTypeId, hold.Quantity, hold.Status.ToString(), hold.ExpiresAt);
}
