using System.Diagnostics.Metrics;
using System.Threading;

namespace TicketingPlatform.Application.Common;

/// <summary>
/// Domain-level metrics. OpenTelemetry subscribes to this meter in the Api composition root.
/// Keep names stable: dashboards and alerts bind to them.
/// </summary>
public static class TicketingMetrics
{
    public const string MeterName = "TicketingPlatform";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> HoldAttempts =
        Meter.CreateCounter<long>("ticketing.holds.attempts");

    public static readonly Counter<long> HoldConflicts =
        Meter.CreateCounter<long>("ticketing.holds.conflicts");

    public static readonly Counter<long> OrdersConfirmed =
        Meter.CreateCounter<long>("ticketing.orders.confirmed");

    public static readonly Counter<long> OrdersPaymentDeclined =
        Meter.CreateCounter<long>("ticketing.orders.payment_declined");

    public static readonly Counter<long> OrdersPaymentUnavailable =
        Meter.CreateCounter<long>("ticketing.orders.payment_unavailable");

    public static readonly Counter<long> OrdersRefunded =
        Meter.CreateCounter<long>("ticketing.orders.refunded");

    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("ticketing.outbox.published");

    public static readonly Counter<long> TicketsScanned =
        Meter.CreateCounter<long>("ticketing.tickets.scanned");

    // --- Messaging delivery (PR 3 hardening): make the outbox/broker path observable so an
    // operator can see backlog, returns, retries, quarantines, and confirmation latency. ---

    /// <summary>Publishes RabbitMQ returned as unroutable or nacked (a topology gap or outage).</summary>
    public static readonly Counter<long> OutboxPublishReturned =
        Meter.CreateCounter<long>("ticketing.outbox.publish_returned");

    /// <summary>Outbox rows rescheduled for a later publish attempt after a failed confirmation.</summary>
    public static readonly Counter<long> OutboxRetried =
        Meter.CreateCounter<long>("ticketing.outbox.retried");

    /// <summary>Outbox rows parked after exhausting their publish-attempt budget or being invalid.</summary>
    public static readonly Counter<long> OutboxQuarantined =
        Meter.CreateCounter<long>("ticketing.outbox.quarantined");

    /// <summary>Time from starting a publish to the broker's confirmation.</summary>
    public static readonly Histogram<double> OutboxConfirmLatency =
        Meter.CreateHistogram<double>("ticketing.outbox.confirm_latency", "ms");

    /// <summary>Consumer deliveries rescheduled onto a durable retry queue after a transient failure.</summary>
    public static readonly Counter<long> ConsumerRetried =
        Meter.CreateCounter<long>("ticketing.consumer.retried");

    /// <summary>Consumer deliveries dead-lettered (poison on first sight, or retry budget exhausted).</summary>
    public static readonly Counter<long> ConsumerDeadLettered =
        Meter.CreateCounter<long>("ticketing.consumer.dead_lettered");

    private static long _outboxBacklogAgeMs;

    /// <summary>The dispatcher records the age of the oldest undelivered outbox row each poll.</summary>
    public static void SetOutboxBacklogAgeMs(long ageMs) => Interlocked.Exchange(ref _outboxBacklogAgeMs, ageMs);

    /// <summary>Age of the oldest still-undelivered outbox row: the operator's stuck-pipeline signal.</summary>
    public static readonly ObservableGauge<long> OutboxBacklogAge =
        Meter.CreateObservableGauge("ticketing.outbox.backlog_age",
            () => Interlocked.Read(ref _outboxBacklogAgeMs), "ms");

    // --- Operational observability (PR 6 §6.4): reconciliation backlogs, expiry lag, admission
    // rate, scan conflicts, and retention, so an operator can alert on a stalling background tier. ---

    /// <summary>Concurrent scanners racing one code: one wins, the losers land here (double-entry attempts).</summary>
    public static readonly Counter<long> TicketScanConflicts =
        Meter.CreateCounter<long>("ticketing.tickets.scan_conflicts");

    /// <summary>Visitors admitted from the waiting room. Its rate IS the actual global admission rate.</summary>
    public static readonly Counter<long> WaitingRoomAdmitted =
        Meter.CreateCounter<long>("ticketing.waiting_room.admitted");

    /// <summary>Rows deleted by the retention sweep, tagged by table.</summary>
    public static readonly Counter<long> RetentionRowsPruned =
        Meter.CreateCounter<long>("ticketing.retention.rows_pruned");

    private static long _waitingRoomDepth;
    /// <summary>The admitter records total visitors still queued across all active events each tick.</summary>
    public static void SetWaitingRoomDepth(long depth) => Interlocked.Exchange(ref _waitingRoomDepth, depth);
    public static readonly ObservableGauge<long> WaitingRoomDepth =
        Meter.CreateObservableGauge("ticketing.waiting_room.depth", () => Interlocked.Read(ref _waitingRoomDepth));

    private static long _paymentReconciliationBacklog;
    /// <summary>Orders whose payment lease has expired and are awaiting reconciliation.</summary>
    public static void SetPaymentReconciliationBacklog(long count) => Interlocked.Exchange(ref _paymentReconciliationBacklog, count);
    public static readonly ObservableGauge<long> PaymentReconciliationBacklog =
        Meter.CreateObservableGauge("ticketing.payments.reconciliation_backlog",
            () => Interlocked.Read(ref _paymentReconciliationBacklog));

    private static long _refundReconciliationBacklog;
    /// <summary>Orders with a stale refund claim awaiting reconciliation.</summary>
    public static void SetRefundReconciliationBacklog(long count) => Interlocked.Exchange(ref _refundReconciliationBacklog, count);
    public static readonly ObservableGauge<long> RefundReconciliationBacklog =
        Meter.CreateObservableGauge("ticketing.refunds.reconciliation_backlog",
            () => Interlocked.Read(ref _refundReconciliationBacklog));

    private static long _holdExpiryLagMs;
    /// <summary>How overdue the oldest just-expired hold was: the expiry worker's falling-behind signal.</summary>
    public static void SetHoldExpiryLagMs(long ageMs) => Interlocked.Exchange(ref _holdExpiryLagMs, ageMs);
    public static readonly ObservableGauge<long> HoldExpiryLag =
        Meter.CreateObservableGauge("ticketing.holds.expiry_lag",
            () => Interlocked.Read(ref _holdExpiryLagMs), "ms");
}
