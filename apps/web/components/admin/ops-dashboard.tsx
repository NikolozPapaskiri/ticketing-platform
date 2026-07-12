"use client";

import { useQuery } from "@tanstack/react-query";
import { getOpsSnapshot } from "@/lib/client-api";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

function tone(status: string) {
  const s = status.toLowerCase();
  if (s === "healthy") return "text-emerald-600";
  if (s === "degraded") return "text-amber-600";
  return "text-destructive";
}

function Stat({ label, value, warn, hint }: { label: string; value: string | number; warn?: boolean; hint?: string }) {
  return (
    <div className="rounded-[8px] border border-border bg-card p-4">
      <p className="text-xs font-medium uppercase tracking-[0.14em] text-muted-foreground">{label}</p>
      <p className={`mt-1 text-2xl font-semibold ${warn ? "text-destructive" : ""}`}>{value}</p>
      {hint ? <p className="mt-1 text-xs text-muted-foreground">{hint}</p> : null}
    </div>
  );
}

export function OpsDashboard() {
  const ops = useQuery({ queryKey: ["admin-ops"], queryFn: getOpsSnapshot, refetchInterval: 15000 });

  if (ops.isLoading) {
    return <p className="text-sm text-muted-foreground">Loading operations snapshot…</p>;
  }
  if (ops.isError || !ops.data) {
    return <p className="text-sm text-destructive">Could not load the operations snapshot.</p>;
  }

  const s = ops.data;
  const oldest = s.outboxOldestPendingAgeSeconds;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <p className="text-sm font-medium uppercase tracking-[0.18em] text-primary">Platform Admin</p>
          <h1 className="mt-2 text-3xl font-semibold">Operations</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Live snapshot · role {s.hostRole} · {new Date(s.generatedAt).toLocaleTimeString()} · refreshes every 15s
          </p>
        </div>
        <Badge variant="secondary" className={tone(s.overallStatus)}>{s.overallStatus}</Badge>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Dependencies</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
          {s.dependencies.map((dep) => (
            <div key={dep.name} className="flex items-center justify-between rounded-[8px] border border-border bg-card p-3">
              <div>
                <p className="font-medium capitalize">{dep.name}</p>
                {dep.description ? <p className="text-xs text-muted-foreground">{dep.description}</p> : null}
              </div>
              <span className={`text-sm font-semibold ${tone(dep.status)}`}>{dep.status}</span>
            </div>
          ))}
        </CardContent>
      </Card>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <Stat label="Waiting room depth" value={s.waitingRoomDepth} />
        <Stat label="Payments awaiting reconciliation" value={s.paymentsAwaitingReconciliation} warn={s.paymentsAwaitingReconciliation > 0} />
        <Stat label="Refunds pending" value={s.refundsPending} warn={s.refundsPending > 0} />
        <Stat label="Outbox pending" value={s.outboxPending} hint={oldest != null ? `oldest ${Math.round(oldest)}s` : undefined} />
        <Stat label="Outbox quarantined" value={s.outboxQuarantined} warn={s.outboxQuarantined > 0} />
        <Stat label="Dead-letter depth" value={s.deadLetterDepth ?? "—"} warn={(s.deadLetterDepth ?? 0) > 0} hint={s.deadLetterDepth == null ? "broker unreachable" : undefined} />
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Orders by status</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          {Object.keys(s.ordersByStatus).length === 0 ? (
            <p className="text-sm text-muted-foreground">No orders yet.</p>
          ) : (
            Object.entries(s.ordersByStatus).map(([status, count]) => (
              <Badge key={status} variant="secondary">{status}: {count}</Badge>
            ))
          )}
        </CardContent>
      </Card>

      <p className="text-xs text-muted-foreground">
        This is the at-a-glance view. Full metrics, logs, and traces live in Grafana (see the observability stack).
      </p>
    </div>
  );
}
