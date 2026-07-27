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

    /// <summary>Crash the save that flips an order to Refunded (after the provider refunded).</summary>
    public bool FailNextRefundSettleSave { get; set; }

    public AsyncGate IdempotencyClaimGate { get; } = new();

    /// <summary>Blocks the save that flips a ticket to Scanned (concurrent-scan coordination).</summary>
    public AsyncGate TicketScanGate { get; } = new();

    /// <summary>Blocks the save that flips a hold to Released (concurrent-release coordination).</summary>
    public AsyncGate HoldReleaseGate { get; } = new();

    /// <summary>
    /// Blocks the save that pushes out a PaymentPending hold's lease. That is the ambiguous-payment
    /// path (provider unreachable), where a client retry and the background reconciler can both be
    /// extending the same lease - holding it here lets a test bump the row underneath and force the
    /// concurrency conflict deterministically instead of hoping for it.
    /// </summary>
    public AsyncGate PaymentLeaseExtendGate { get; } = new();

    public sealed class SimulatedCrashException : Exception
    {
        public SimulatedCrashException()
            : base("Simulated crash after provider success, before the confirmation commit.") { }
    }

    public void Reset()
    {
        FailNextOrderConfirmSave = false;
        FailNextRefundSettleSave = false;
        IdempotencyClaimGate.Arm(0);
        TicketScanGate.Arm(0);
        HoldReleaseGate.Arm(0);
        PaymentLeaseExtendGate.Arm(0);
    }

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
        if (IsScanningTicket(eventData.Context))
            await TicketScanGate.PassAsync(ct);
        if (IsReleasingHold(eventData.Context))
            await HoldReleaseGate.PassAsync(ct);
        if (IsExtendingPaymentLease(eventData.Context))
            await PaymentLeaseExtendGate.PassAsync(ct);
        return await base.SavingChangesAsync(eventData, result, ct);
    }

    private static bool IsScanningTicket(DbContext? context) =>
        context is not null && context.ChangeTracker.Entries<Ticket>()
            .Any(e => e.State == EntityState.Modified && e.Entity.Status == TicketStatus.Scanned);

    /// <summary>
    /// An EXTENSION, not the initial claim. Both saves leave the hold PaymentPending with a modified
    /// lease - the claim (Active -> PaymentPending) also writes Status, an extension does not. Without
    /// that distinction this gate catches the claim instead, and the test ends up exercising the
    /// "hold no longer available" 409 path rather than the ambiguous-payment one.
    /// </summary>
    private static bool IsExtendingPaymentLease(DbContext? context) =>
        context is not null && context.ChangeTracker.Entries<Hold>()
            .Any(e => e.State == EntityState.Modified
                      && e.Entity.Status == HoldStatus.PaymentPending
                      && e.Property(h => h.PaymentLeaseUntil).IsModified
                      && !e.Property(h => h.Status).IsModified);

    private static bool IsReleasingHold(DbContext? context) =>
        context is not null && context.ChangeTracker.Entries<Hold>()
            .Any(e => e.State == EntityState.Modified && e.Entity.Status == HoldStatus.Released);

    private void ThrowIfConfirming(DbContext? context)
    {
        if (context is null)
            return;

        if (FailNextOrderConfirmSave && HasOrderInStatus(context, OrderStatus.Confirmed))
        {
            FailNextOrderConfirmSave = false; // fire exactly once
            throw new SimulatedCrashException();
        }

        if (FailNextRefundSettleSave && HasOrderInStatus(context, OrderStatus.Refunded))
        {
            FailNextRefundSettleSave = false;
            throw new SimulatedCrashException();
        }
    }

    private static bool HasOrderInStatus(DbContext context, OrderStatus status) =>
        context.ChangeTracker.Entries<Order>()
            .Any(e => e.State is EntityState.Added or EntityState.Modified && e.Entity.Status == status);

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
