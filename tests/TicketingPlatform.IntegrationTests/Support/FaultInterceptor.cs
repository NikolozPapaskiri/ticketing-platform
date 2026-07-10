using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TicketingPlatform.Domain;

namespace TicketingPlatform.IntegrationTests.Support;

/// <summary>
/// Test-only EF <see cref="SaveChangesInterceptor"/> used to make two otherwise-timing-dependent
/// races exact:
///  - <see cref="FailNextOrderConfirmSave"/>: throws on the save that flips an order to Confirmed,
///    simulating a crash AFTER the provider charged but BEFORE the final commit. The earlier
///    idempotency-claim save (an InProgress record, no confirmed order) is left untouched.
///  - <see cref="IdempotencyClaimGate"/>: blocks the idempotency-claim save so two concurrent
///    requests with the same key can be held until BOTH are past the "record not found" check,
///    forcing the unique-index insert collision deterministically.
/// Registered as an <see cref="IInterceptor"/> singleton; EF discovers it from the app container.
/// </summary>
public sealed class FaultInterceptor : SaveChangesInterceptor
{
    public bool FailNextOrderConfirmSave { get; set; }
    public AsyncGate IdempotencyClaimGate { get; } = new();

    public sealed class SimulatedCrashException : Exception
    {
        public SimulatedCrashException()
            : base("Simulated crash after provider success, before the confirmation commit.") { }
    }

    public void Reset() => FailNextOrderConfirmSave = false;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfConfirming(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        ThrowIfConfirming(eventData.Context);
        if (IsIdempotencyClaim(eventData.Context))
            await IdempotencyClaimGate.PassAsync(ct);
        return await base.SavingChangesAsync(eventData, result, ct);
    }

    private void ThrowIfConfirming(DbContext? context)
    {
        if (!FailNextOrderConfirmSave || context is null)
            return;

        var confirming = context.ChangeTracker.Entries<Order>()
            .Any(e => e.State is EntityState.Added or EntityState.Modified
                      && e.Entity.Status == OrderStatus.Confirmed);
        if (!confirming)
            return;

        FailNextOrderConfirmSave = false; // fire exactly once
        throw new SimulatedCrashException();
    }

    private static bool IsIdempotencyClaim(DbContext? context)
    {
        if (context is null)
            return false;

        var hasAddedClaim = context.ChangeTracker.Entries<IdempotencyRecord>()
            .Any(e => e.State == EntityState.Added && e.Entity.Status == IdempotencyRecordStatus.InProgress);
        var hasConfirmedOrder = context.ChangeTracker.Entries<Order>()
            .Any(e => e.Entity.Status == OrderStatus.Confirmed);

        // The claim save adds an InProgress record and no confirmed order; the final save has
        // a confirmed order and only MODIFIES the record - never gate the final save.
        return hasAddedClaim && !hasConfirmedOrder;
    }
}
