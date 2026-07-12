# Architecture and design decisions

Written to be defended out loud in an interview. Every claim maps to code in this repo.

## The shape of the system

A modular monolith on ASP.NET Core 10 / EF Core 10 / PostgreSQL, layered Clean Architecture,
event-driven internally via a transactional outbox and RabbitMQ.

```
Api            controllers, minimal-API slices, middleware, SignalR hub, DI composition
  -> Application   use-case services, ports (repositories, gateways, cache, storage), DTOs, validators
       -> Domain      entities, the Event/Hold/Order state machines, invariants (zero dependencies)
Infrastructure  EF Core, repositories, payment client, Redis, RabbitMQ, background services (implements Application ports)
```

Dependencies point inward. Nothing references Api; Domain references nothing. The Api project
contains **zero EF Core usage** (grep-verified) except the one deliberate vertical slice.

The web product is a separate Next.js app in `apps/web`, not another domain layer:

```text
Browser
  -> Next.js UI
      -> Next.js route handlers (BFF, HttpOnly token cookies)
          -> ASP.NET Core API
  -> SignalR availability hub directly
```

The BFF owns browser session transport: access and refresh tokens stay in HttpOnly cookies,
REST calls are server-to-server, and the browser only connects directly to `/hubs/availability`
because that hub is anonymous and CORS-scoped. `/account`, `/organizer`, and `/admin` have UI
role guards for navigation, but the API authorization policies remain the real security wall.

Frontend operational rules:

- Public routes: `/`, `/t/{slug}`, `/t/{slug}/events/{eventId}`.
- Customer route: `/account`.
- Organizer route: `/organizer`.
- Platform admin route: `/admin`.
- Local browser testing uses `http://localhost:3000`, not `http://127.0.0.1:3000`, because Next
  dev assets/HMR expect the same localhost origin used by Playwright and the app config.
- Local HTTP uses `COOKIE_SECURE=false`; production HTTPS should keep secure cookies enabled.

## Clean Architecture vs Vertical Slice — and where each lives here

**Clean Architecture** is the default for this codebase because the domain is long-lived and
shared: the oversell rules, the booking saga, and the event state machine are touched by many
features and must be unit-testable in isolation and impossible to bypass. The dependency rule
(implemented as project references) makes "a business rule accidentally depends on EF" a
compile error, not a code-review catch.

**Vertical Slice** is used for exactly one feature: the event sales report
(`Api/Features/SalesReport/GetEventSalesReport.cs`). It is a read-only leaf that nothing else
consumes, so the layered ceremony (controller -> service -> repository port -> EF impl, four
projects) would be pure indirection. The slice puts route, handler, query, and response in one
file, and reaches the `DbContext` directly.

The honest senior position is not "pick one" but **know where each fits**:

| | Clean Architecture | Vertical Slice |
| --- | --- | --- |
| Optimizes for | a protected, shared, testable domain | feature locality and delivery speed |
| Cost | more projects, mapping, ceremony | duplication; no central rule enforcement |
| Right for | business rules many features share | leaf features (reports, exports, admin) |
| Wrong for | a trivial CRUD app | anything holding invariants others depend on |

The failure mode of all-slices: the moment two slices need the same rule, you extract it, and
you have reinvented the Application layer one painful step at a time. The failure mode of
all-layers: a five-project ceremony wrapped around a query nobody reuses. This repo does both,
on purpose, and the slice file documents its own trade-off in its header.

## Monolith vs Microservices — why this stays one deployable

This is a **modular monolith and should remain one** for a team of its likely size.
Microservices buy independent deploy/scale and team autonomy at the cost of distributed-systems
complexity (network partitions, distributed transactions, versioned contracts, operational
surface). This system has none of the problems that justify that cost: one team, one scaling
profile, no organizational seams demanding independent release cadence.

What earns the "I would not split this yet, and here is exactly when I would" answer:

- **The seams already exist.** Ticketing/inventory, payments, and notifications communicate
  through domain events over the outbox + broker, not in-process method calls. Extracting the
  notification service (or the ticket issuer) into its own process is a deployment change, not a
  rewrite, because they already consume `OrderConfirmed` from RabbitMQ.
- **When I would split:** when a module develops a genuinely different scaling profile (a search
  read-side that needs 10x the replicas), or a separate team needs an independent release
  cadence, or a compliance boundary forces isolation. Not before.

Restraint is the senior signal. Splitting to fix messy code produces distributed messy code.

### One deployable, two deployments (the API/worker split)

The modular monolith stays one codebase and one image, but it runs as **two deployments** chosen by
a single `Hosting:Role` setting (`Api` / `Worker` / `All`). The API pods serve HTTP and run no
background workers; a separate worker deployment runs the outbox dispatcher, the consumers, hold
expiry, payment/refund reconciliation, the waiting-room admission valve, and the retention sweep.
This is not a microservice split — no business ownership moves, there are no new network contracts —
it just stops HTTP scaling from multiplying an admission valve and scheduled scans, and lets the two
tiers scale on their own signals. Readiness is role-aware to match: RabbitMQ is an **asynchronous**
dependency for the API (a broker outage buffers the transactional outbox, so API pods keep serving)
but a **hard** dependency for a worker, which cannot function without it. Blobs (ticket PDFs, event
images) live in shared object storage (S3/MinIO) behind `IFileStorage`, so no pod-local disk or
ReadWriteOnce volume constrains replica placement.

## The decisions that matter, each with its trade-off

- **Multi-tenancy: shared schema + EF global query filters.** Cheapest to run, one migration set,
  and no developer can forget the tenant predicate (it is injected by the filter). Cross-tenant
  reads return 404, not 403 — the row is invisible, so its existence never leaks. Trade-off vs
  schema/DB-per-tenant: weaker blast-radius isolation, mitigated by the filter being
  non-optional. The tenant comes from a signed `tenant_id` JWT claim, so a client cannot choose
  its tenant.

- **Oversell prevention: three strategies, one interface.** `IReservationStrategy` has optimistic
  (xmin + retry, the default), pessimistic (`SELECT ... FOR UPDATE`), and Redis-atomic
  implementations, switchable by config. Optimistic wins when conflicts are rare; pessimistic
  when they are the norm (a flash sale); Redis-atomic when the database must leave the hot path
  entirely, at the cost of two stores that must reconcile. Proven by a 30-buyers-vs-10-tickets
  test that never oversells.

- **Transactional outbox for the dual-write problem.** A DB change and its event publish cannot
  be atomic across Postgres and RabbitMQ. The event is written to an outbox table in the same
  transaction as the state change; a dispatcher publishes it afterward. At-least-once by
  construction, so every consumer dedupes by message id (per-consumer, for fan-out safety) and
  poison messages dead-letter instead of looping. Producers use typed `IIntegrationEvent`
  records, not event-name strings and anonymous objects. RabbitMQ bodies use a versioned envelope
  (`messageId`, `eventType`, `schemaVersion`, `occurredAt`, `tenantId`, `correlationId`, `payload`),
  and consumers reject identity/routing/version mismatches before side effects. Publisher confirms,
  mandatory routing, persistent retry scheduling, per-consumer TTL retry queues, and an attempt
  cap close the common broker loss and hot-loop windows.

- **CQRS read model for availability.** Browse reads hit a denormalized `EventAvailabilityView`
  maintained by a projection consumer, never the contested `Inventories` row. Eventually
  consistent — the deliberate price — and self-healing because the projection re-reads live
  truth rather than applying deltas (a lost or reordered event only means brief staleness).

- **Result pattern over exceptions for expected failures.** Not-found, conflict, and declined-
  payment are outcomes, not exceptions; controllers map the `Result` error kind to HTTP.
  Exceptions stay for the genuinely unexpected. Validation runs in a global action filter.

- **Resilience with idempotency.** The payment client retries 5xx with backoff behind a circuit
  breaker, but every charge carries an idempotency key (the order id), so a retried timeout can
  never double-charge; 4xx declines are never retried; an outage returns a typed 503, never a
  crash.

- **Real-time that survives replicas.** SignalR pushes live availability through a Redis
  backplane, so a broadcast from pod B reaches a client connected to pod A. The same
  "in-process state breaks at two replicas" lesson governs the cache (Redis, not IMemoryCache)
  and the locks (the DB token, not a process lock).

- **Durable payment and money invariants (post-`v3` hardening).** Checkout claims the hold
  (`Active → PaymentPending`) and persists the order BEFORE the charge, so a payment in flight
  can't be oversold or lost; recovery is by the order id (the stable provider key) via a retry or
  the reconciler. Every post-payment transition is a compare-and-swap: refund claims
  `Confirmed → RefundPending` with a stable `refund:{orderId}` key so customer and staff can't
  double-refund; ticket scan is an `xmin`-guarded `Issued → Scanned` so two scanners admit once;
  hold release credits inventory exactly once under a release/expiry race.

- **Scanned-ticket refund policy: a scanned ticket is non-refundable.** Chosen over
  allow-with-audit and staff-override because admission consumes the good — the simplest rule to
  reason about and enforce. The refund path rejects a `Scanned` ticket with a 409; the decision is
  revisitable (an authorized staff-override could be layered on later without changing the model).

## Testing strategy

Many fast unit tests on domain/application logic (the state machine, reservation math,
validators — no DB, no HTTP). Integration tests against **real** Postgres, Redis, and RabbitMQ
via Testcontainers, driving the actual API through `WebApplicationFactory` and authenticating
like a real client. `DbContext` is never mocked. The backend has 149 tests total. The frontend
adds typecheck, lint, production build, npm audit, and a Playwright golden journey that seeds
through the API and then drives the real browser flow.

## What was deliberately deferred (and why that is a feature)

Reserved-seating maps and search at scale are scoped
as design write-ups rather than half-built code. One finished, defensible system beats several
abandoned ones — and knowing what to leave out is itself the judgment this project is meant to
demonstrate.
