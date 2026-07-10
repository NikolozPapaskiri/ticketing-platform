using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Infrastructure.Reservations;

/// <summary>
/// Optimistic concurrency (the DEFAULT strategy). No locks: read the row, decrement in memory,
/// save. The Inventory entity carries Postgres's xmin system column as a concurrency token, so
/// EF appends "WHERE xmin = [value we read]" to the UPDATE. If another buyer committed in
/// between, xmin changed, zero rows match, EF throws DbUpdateConcurrencyException - and we
/// retry against reloaded values. The LAST ticket can never be sold twice: one of two racing
/// commits always loses.
/// Trade-off: under extreme contention on one row most attempts lose and retry (wasted work);
/// that is exactly when PessimisticLock or RedisAtomic earn their keep.
/// </summary>
public sealed class OptimisticReservationStrategy : IReservationStrategy
{
    private const int MaxAttempts = 3;

    private readonly TicketingDbContext _db;
    public OptimisticReservationStrategy(TicketingDbContext db) => _db = db;

    public async Task<Result> ReserveAsync(Hold hold, CancellationToken ct)
    {
        // Tenant-scoped tracked load (identity map: same instance the service pre-checked).
        var inventory = await _db.Inventories.FirstOrDefaultAsync(i => i.TicketTypeId == hold.TicketTypeId, ct);
        if (inventory is null)
            return Result.NotFound($"Ticket type '{hold.TicketTypeId}' was not found.");

        _db.Holds.Add(hold);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (!inventory.TryReserve(hold.Quantity))
            {
                _db.Holds.Remove(hold);
                return Result.Conflict(
                    $"Cannot hold {hold.Quantity} tickets; only {inventory.AvailableQuantity} available.");
            }

            try
            {
                // Decrement + hold row in ONE transaction; the xmin check guards the decrement.
                await _db.SaveChangesAsync(ct);
                return Result.Success();
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxAttempts)
            {
                // Someone else committed between our read and our write. Reload ONLY the
                // conflicting inventory entry (fresh values + fresh xmin) and re-check.
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Retries exhausted: extreme contention. Fail sideways as a conflict the buyer
                // can retry, never as an oversell.
                _db.Holds.Remove(hold);
                return Result.Conflict("The tickets are being contested right now; please try again.");
            }
        }

        _db.Holds.Remove(hold);
        return Result.Conflict("The tickets are being contested right now; please try again.");
    }

    public async Task ReleaseAsync(Hold hold, CancellationToken ct)
    {
        // The give-back also races (another buyer may be decrementing) - same retry discipline.
        for (var attempt = 1; ; attempt++)
        {
            hold.TicketType.Inventory.Release(hold.Quantity);
            try
            {
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxAttempts)
            {
                // If the HOLD row conflicted, another release or expiry already flipped it AND
                // credited inventory. This attempt must NOT credit a second time - the atomic
                // SaveChanges rolled our increment back, so just stop (idempotent). Retrying is
                // only correct for an inventory-only conflict (a concurrent reserve), where we
                // re-apply our single credit against reloaded state. This is the fix for the
                // double-credit the old unconditional retry produced under a release race.
                if (ex.Entries.Any(e => e.Entity is Hold))
                    return;
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(ct);
            }
        }
    }
}
