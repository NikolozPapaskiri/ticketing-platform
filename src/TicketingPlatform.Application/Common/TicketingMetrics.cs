using System.Diagnostics.Metrics;

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
}
