namespace TicketingPlatform.Api.Features.Ops;

/// <summary>
/// A live operations snapshot for the in-app admin ops page. Computed from the SOURCE OF TRUTH
/// (health checks + DB + Redis + RabbitMQ) rather than the in-process metric gauges, because those
/// gauges are populated in the worker process - so this stays accurate whatever the deployment
/// topology, and needs no Prometheus. Grafana remains the deep tool; this is the at-a-glance view.
/// </summary>
public sealed record OpsSnapshot(
    DateTimeOffset GeneratedAt,
    string HostRole,
    string OverallStatus,
    IReadOnlyList<DependencyHealth> Dependencies,
    long WaitingRoomDepth,
    long PaymentsAwaitingReconciliation,
    long RefundsPending,
    long OutboxPending,
    long OutboxQuarantined,
    double? OutboxOldestPendingAgeSeconds,
    long? DeadLetterDepth,
    IReadOnlyDictionary<string, long> OrdersByStatus);

/// <summary>One dependency's readiness, as reported by the health-check registrations.</summary>
public sealed record DependencyHealth(string Name, string Status, string? Description);
