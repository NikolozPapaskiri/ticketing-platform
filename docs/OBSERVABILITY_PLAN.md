# Observability plan — a monitoring surface for everything

Status: P1–P4 done (in-app ops page + metrics/logs/traces stack + infra exporters); P5 (alerting +
k8s overlay) outstanding
Created: 2026-07-13
Builds on: the `TicketingPlatform` OpenTelemetry meter, the `/health/detail` endpoint, and the
distributed tracing already wired for HTTP in/out, Npgsql SQL, and the RabbitMQ hop.

## Known gaps / next

- **P5 outstanding:** alert rules (Grafana Alerting / Alertmanager), a k8s `monitoring` overlay, and
  the remaining curated dashboards (only "Ticketing — Overview" exists; checkout, messaging,
  runtime/infra, and business-KPI dashboards are still to author).
- **Not yet runtime-proven end to end:** the stack configs are validated against the real
  collector/Prometheus/Loki/Tempo binaries and the merged compose is valid, but the full 12-container
  stack has not been brought up with live traffic - so "data actually populating the Grafana panels"
  is unconfirmed. The overview dashboard's metric names assume `add_metric_suffixes: false` on the
  collector's Prometheus exporter and may need a panel tweak once real series are visible.
- **Branch CI is red** because it is stacked on `feature/production-operations`, whose §6.5 CI has two
  failing jobs (Trivy action version, e2e first run) - see `docs/PRODUCTION_SAFETY_HARDENING_PLAN.md`.
  Those must be fixed for this PR to go green; nothing in the observability code itself is implicated.

## Objective

The app already *emits* metrics and traces over OTLP, but nothing collects or displays them. Give
the platform a real monitoring surface across the four pillars — **metrics, logs, traces, health** —
and a curated in-app operations page for staff who will not open Grafana.

## Guiding choices

- **Self-hosted, free/OSS, local Docker + k8s** — matches the project's no-cloud-spend rule. Managed
  equivalents (Grafana Cloud, Datadog) are the paid path and are not needed.
- **The app already speaks OTLP**, so we add a collector + backends rather than re-instrument code.
- **Grafana is "the page"** — one pane of glass over metrics, logs, and traces. An in-app admin
  page complements it with a curated, business-friendly snapshot.

## The stack

```
App (OTLP push: metrics + traces + logs)
        │
        ▼
OpenTelemetry Collector ─┬→ Prometheus   (metrics store; also scrapes infra exporters)
                         ├→ Tempo        (traces store)
                         └→ Loki         (logs store)
                                  │
                                  ▼
                               Grafana   (dashboards + alerting = the page)
```

Infra exporters Prometheus scrapes directly: **postgres_exporter**, **redis_exporter**, RabbitMQ's
built-in **Prometheus plugin**, MinIO's built-in **`/minio/v2/metrics/cluster`**, and in k8s
**kube-state-metrics + cAdvisor**.

All components are free/OSS containers, the same footprint class as the existing
Postgres/Redis/RabbitMQ/MinIO services.

## The four pillars — everything that can be monitored

### 1. Metrics — custom (`TicketingPlatform` meter)

| Domain | Instruments |
|---|---|
| Checkout / payments | `orders.confirmed` / `.payment_declined` / `.payment_unavailable` / `.refunded`, `payments.reconciliation_backlog`, `refunds.reconciliation_backlog` |
| Inventory / holds | `holds.attempts`, `holds.conflicts`, `holds.expiry_lag` |
| Tickets | `tickets.scanned`, `tickets.scan_conflicts` |
| Waiting room | `waiting_room.admitted` (rate = actual admission rate), `waiting_room.depth` |
| Messaging / outbox | `outbox.published` / `.publish_returned` / `.retried` / `.quarantined`, `outbox.backlog_age`, `outbox.confirm_latency` (histogram), `consumer.retried` / `.dead_lettered` |
| Data hygiene | `retention.rows_pruned` (tagged by table) |

### 2. Metrics — auto-instrumented (already emitted; just collect)

- HTTP server (RED): request rate, error %, p50/p95/p99 latency by route + status.
- HTTP client (payment provider): rate, latency, errors.
- .NET runtime: GC pauses/heap, thread-pool queue, exceptions, lock contention.

### 3. Metrics — infrastructure (via exporters)

- **PostgreSQL:** connections, TPS, locks/deadlocks, cache-hit ratio, slow queries, bloat.
- **Redis:** memory, ops/sec, evictions, keyspace, clients.
- **RabbitMQ:** per-queue depth (esp. **DLQ depth**), publish/deliver rates, unacked, memory.
- **MinIO:** bucket size/object count, request rate, errors.
- **Containers/pods (k8s):** CPU/mem vs limits, restarts, OOMKills.

### 4. Logs (Loki) & traces (Tempo)

- Structured logs searchable by `correlationId` / `traceId` (already stamped by the correlation-id
  middleware); shipped to Loki via the OTLP log pipeline.
- Distributed traces span HTTP-in → Npgsql SQL → the RabbitMQ hop → consumer. Exemplars link a slow
  latency panel straight to the trace in Tempo.

### 5. Health / uptime

- Blackbox probing of `/health/ready` and `/health/detail` per role (API vs worker).

## Dashboards (the pages)

1. **Overview / golden signals** — API RED, error budget, dependency-health tiles, active alerts.
2. **Checkout & payments** — order-outcome funnel, reconciliation backlogs, provider latency/errors.
3. **Messaging & outbox** — backlog age, returns/retries/quarantine, confirm-latency percentiles,
   consumer retries, **DLQ depth**.
4. **Waiting room** — admission rate vs configured rate, queue depth, join throttling.
5. **Inventory & holds** — attempts vs conflicts, **expiry lag**, scans vs scan-conflicts.
6. **Runtime & infrastructure** — .NET runtime + Postgres/Redis/RabbitMQ/MinIO + pod resources.
7. **Retention & data hygiene** — rows pruned by table, table-growth trend.
8. **Business KPIs** — hold→order conversion, revenue, refund rate, oversell attempts blocked.

## Alerting (Grafana Alerting / Alertmanager)

Page-worthy conditions: DLQ depth > 0 · reconciliation backlog rising and not draining · outbox
backlog age high · hold expiry lag high · API error-rate / latency SLO burn · `/health/ready`
failing · admission rate deviating from config · Postgres connections saturating.

## Second surface — the in-app "Operations" page

A **PlatformAdmin-only page under `apps/web/app/admin`** backed by a read-only
`GET /api/v1/admin/ops` endpoint. Because the metric gauges are populated in the *worker* process,
the endpoint computes a live snapshot from the **source of truth** (Redis waiting-room depth, DB
reconciliation/outbox backlogs and order-status breakdown, RabbitMQ DLQ depth) plus the
`/health/detail` dependency status — so it is accurate in any deployment topology and does not
require Prometheus to be up. Grafana remains the deep tool; this is the curated at-a-glance view for
on-call staff.

## Rollout

- **P1 — in-app ops page:** ✅ `GET /api/v1/admin/ops` snapshot + the `/admin/ops` page.
- **P2 — make metrics visible:** ✅ OTel Collector + Prometheus + Grafana in the
  `docker-compose.observability.yml` overlay, with a provisioned "Ticketing — Overview" dashboard.
- **P3 — logs + traces:** ✅ Loki + Tempo added; the app ships logs via OTLP (trace id attached).
- **P4 — infra exporters:** ✅ postgres_exporter + redis_exporter + the RabbitMQ Prometheus plugin +
  MinIO metrics, all scraped by Prometheus.
- **P5 — alerting + k8s:** ⬜ alert rules + a k8s `monitoring` overlay + the remaining curated
  dashboards (still to do).

## Cost

Zero — everything self-hosted OSS (OTel Collector, Prometheus, Grafana, Loki, Tempo, the exporters),
run as containers alongside the existing infra. No managed services.
