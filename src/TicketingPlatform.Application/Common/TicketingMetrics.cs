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
}
