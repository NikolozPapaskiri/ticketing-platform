# Ticketing Platform

A multi-tenant event ticketing and booking platform — ASP.NET Core 10, EF Core 10, PostgreSQL,
Clean Architecture. Event organizers (tenants) create events and sell tickets; customers browse
and buy. The interesting part is not CRUD: it is selling a finite, contested resource correctly,
under load, in real time, for many tenants at once.

This is a deliberately staged build (naive → layered → event-driven → production). Each stage is
tagged so the progression is reviewable: `v1-naive` is the un-layered Phase 1; the Clean
Architecture refactor is in progress on `main`. The full plan and current status live in
[CLAUDE.md](CLAUDE.md).

## Architecture

Clean Architecture; dependencies point inward. No project references Api; Domain references nothing.

```
            ┌──────────────────────────────┐
            │  TicketingPlatform.Api       │  controllers, middleware, filters,
            │  (composition root)          │  ProblemDetails, versioning, DI wiring
            └────────────┬─────────────────┘
                         │ references
     ┌───────────────────┼───────────────────────┐
     ▼                                           ▼
┌─────────────────────────────┐   ┌──────────────────────────────────┐
│ TicketingPlatform.          │   │ TicketingPlatform.Infrastructure │
│ Application                 │◄──┤ EF Core DbContext, migrations,   │
│ contracts (DTOs),           │   │ repositories, AddInfrastructure()│
│ validators, ports           │   └──────────────────────────────────┘
│ (ITenantContext, repos)     │
└────────────┬────────────────┘
             ▼
┌─────────────────────────────┐
│ TicketingPlatform.Domain    │  entities, EventStatus state machine,
│ (no dependencies)           │  domain exceptions
└─────────────────────────────┘
```

- **Domain** — `Tenant`, `Event`, `TicketType`, `Inventory`; the guarded `Event` status state
  machine (`Draft → OnSale → Closed`, explicit transition table, terminal `Closed`).
- **Application** — request/response contracts, FluentValidation validators, ports
  (`ITenantContext`; repositories and use-case services are the in-progress final stage).
- **Infrastructure** — `TicketingDbContext` (fluent configuration, global query filters,
  Postgres `xmin` concurrency token), migrations, `AddInfrastructure(connectionString)`.
- **Api** — versioned controllers (`/api/v1/...`), tenant-resolution + correlation-id middleware,
  global `FluentValidationFilter`, `IExceptionHandler` returning RFC 7807 ProblemDetails.

## Key design decisions (the "why", short form)

- **Multi-tenancy: shared schema + EF Core global query filters.** Every tenant-owned read is
  scoped to the current tenant automatically — no hand-written `WHERE TenantId = ...` anywhere.
  Cross-tenant reads return 404 (the row is invisible, not forbidden). The tenant currently comes
  from an `X-Tenant-Id` header via middleware; Phase 3 replaces that with a tenant claim on the
  authenticated principal.
- **State machine on the entity, not in the controller.** `Event.CanTransitionTo/TransitionTo`
  with an explicit allowed-transitions table; controllers pre-check and return **409** RFC 7807
  ProblemDetails for illegal moves; the entity throws `InvalidStatusTransitionException` as a
  defense-in-depth backstop.
- **Uniform error contract.** Every error — validation (400 + per-field map), missing tenant
  (400), conflict (409), unhandled (500) — is RFC 7807 with `type` and `traceId`.
- **Validation as a cross-cutting filter.** FluentValidation validators live in Application; a
  single global action filter validates any bound argument that has a registered `IValidator<T>`.
  Uniqueness (tenant slug) is deliberately NOT a validator rule — that guard belongs to the DB
  unique index (check-then-insert races); the API maps the conflict to 409.
- **Offset pagination with guardrails.** `page` validated, `pageSize` clamped to 100, stable
  ordering with an `Id` tiebreaker, count-then-page. Keyset pagination is the documented upgrade
  path for scale.
- **Optimistic concurrency via Postgres `xmin`** on `Inventory` (declared as a `uint` shadow
  property; Npgsql 10 removed the old `UseXminAsConcurrencyToken()` helper). Foundation for the
  Phase 5 oversell-prevention work.
- **URL-segment API versioning** (`Asp.Versioning`, `api/v{version}`), default v1.0,
  `api-supported-versions` response header.
- **`DateTimeOffset` everywhere** (Npgsql `timestamp with time zone` safety) and manual, explicit
  DTO mapping (no AutoMapper).

## Prerequisites

- **.NET 10 SDK** (pinned by `global.json` to 10.0.300, `rollForward: latestFeature`).
- **Docker** (PostgreSQL 17).
- **EF Core CLI v10**: `dotnet tool update --global dotnet-ef --version 10.0.0`.
- IDE note: building `net10.0` requires MSBuild 18 — **Visual Studio 2026** (or any editor + the
  `dotnet` CLI, which uses the SDK's own MSBuild). Visual Studio 2022 cannot build this solution.

## Run it

**The whole product, one command (containerized API + Postgres + Redis + RabbitMQ):**

```bash
docker compose up -d --build
# API:        http://localhost:5000  (migrates + seeds admin@platform.local / Admin123$ on boot)
# RabbitMQ UI http://localhost:15672 (ticketing / ticketing)
# Probes:     http://localhost:5000/health/live , /health/ready
```

**Development loop (API on the host, infra in containers):**

```bash
docker compose up -d postgres redis rabbitmq
dotnet run --project src/TicketingPlatform.Api    # migrates + seeds in Development
# new migration (cross-project: migrations live in Infrastructure, startup is Api):
dotnet ef migrations add <Name> --project src/TicketingPlatform.Infrastructure --startup-project src/TicketingPlatform.Api
```

API: `http://localhost:5000`, all routes under `/api/v1`. OpenAPI document at
`/openapi/v1.json` in Development (document name `v1` is unrelated to the API version). `GET /`
returns 404 by design — this is an API, not a site.

## Operations (Phase 6)

- **Health probes:** `/health/live` checks nothing external (a dependency outage must fail
  readiness, not liveness — restarting a pod does not fix Postgres); `/health/ready` probes
  Postgres, Redis, and RabbitMQ.
- **Rate limiting:** fixed-window per client IP on the auth endpoints (429 before any password
  hashing runs); limit via `RateLimiting:AuthRequestsPerMinute`.
- **OpenTelemetry:** traces (ASP.NET Core, HttpClient, Npgsql, and the custom messaging source)
  + metrics. The outbox stores the W3C `traceparent`, the dispatcher stamps it into message
  headers, and the consumer rejoins it — **one trace spans HTTP → outbox → RabbitMQ →
  consumer**. Point `Otlp:Endpoint` at a collector (Jaeger, Grafana) to export.
- **Graceful shutdown:** 30s drain on SIGTERM so in-flight sagas finish during rolling updates.
- **CI:** GitHub Actions builds and runs all tests (Testcontainers works on the runners), then
  builds the image and pushes to GHCR from `main`.

### Kubernetes (local cluster: kind or Docker Desktop)

```bash
docker build -t ticketing-api:local .
kind load docker-image ticketing-api:local     # skip on Docker Desktop k8s
kubectl apply -k k8s/
kubectl -n ticketing get pods                  # 2 api replicas + postgres/redis/rabbitmq
kubectl -n ticketing port-forward svc/ticketing-api 5001:80
```

The manifests carry the teaching notes: readiness-vs-liveness, resource requests/limits,
why two API replicas are safe here (nothing correctness-critical lives in pod memory), and
what is dev-cluster-only (emptyDir Postgres, committed dev Secret).

### Tests

```bash
dotnet test   # 79 tests: xUnit unit tests (state machine, reservation math, validators)
              # + Testcontainers integration tests against a throwaway real Postgres
              # (Docker must be running for the integration project)
```

## See multi-tenancy work

Open [`requests.http`](requests.http) (VS 2026 / Rider / VS Code REST Client) and run it top to
bottom:

1. Create two tenants (no tenant header — tenants are the top-level owners).
2. Create an event under each tenant with its `X-Tenant-Id` header.
3. List events as tenant A → only A's events; as tenant B → only B's. No tenant predicate is
   written in any controller — the global query filter injects it (EF SQL logging is on; watch
   the console).
4. The kicker: GET tenant A's event id with tenant B's header → **404**. B cannot even learn the
   row exists.
5. Publish/close an event, then repeat a transition → **409** ProblemDetails from the state
   machine. Try `?status=OnSale&page=1&pageSize=1` on the browse endpoint for paged, filtered
   results.

## API surface (v1)

| Method | Route | Notes |
| --- | --- | --- |
| POST | `/api/v1/tenants` | create tenant; slug conflict → 409 |
| GET | `/api/v1/tenants` | list tenants (admin view, unfiltered) |
| GET | `/api/v1/events?page=&pageSize=&status=` | tenant-scoped, paged, filtered browse |
| GET | `/api/v1/events/{id}` | full event graph (ticket types + inventory) |
| POST | `/api/v1/events` | create (starts `Draft`) |
| POST | `/api/v1/events/{id}/publish` | `Draft → OnSale`, else 409 |
| POST | `/api/v1/events/{id}/close` | `Draft/OnSale → Closed`, else 409 |
| POST | `/api/v1/events/{eventId}/ticket-types` | add ticket type + inventory |
| POST | `/api/v1/holds` | reserve inventory under a TTL; insufficient stock → 409 |
| GET | `/api/v1/holds/{id}` | inspect a hold |
| POST | `/api/v1/holds/{id}/release` | give the quantity back; double release → 409 |
| POST | `/api/v1/orders` | the booking saga: charge the hold, confirm; declined → 409, provider down → 503 |
| GET | `/api/v1/orders/{id}` | inspect an order |
| GET | `/api/v1/orders/{id}/ticket` | download the issued ticket PDF (404 until the async issuer produces it) |
| GET | `/api/v1/events/{id}/availability` | CQRS read model: live availability off the contested write path |
| GET | `/api/v1/events/{id}/sales-report` | vertical-slice feature: confirmed-sales aggregate |
| POST | `/api/v1/auth/register` · `/login` · `/refresh` | customer signup, login, refresh-token rotation |
| POST | `/api/v1/auth/register-staff` | admin-only staff/admin provisioning |

Events/holds/orders require an **OrganizerStaff JWT** (the tenant comes from its signed
`tenant_id` claim); tenants + staff provisioning require **PlatformAdmin**. Anonymous → 401,
wrong role → 403, cross-tenant → 404. All errors are RFC 7807.

## Roadmap (staged; tags mark milestones)

- **Done (Phase 2, tag `v2-clean`):** Clean Architecture (use-case services + repository ports,
  thin controllers, Api free of EF), Testcontainers integration tests, the `Hold` TTL
  reservation concept.
- **Done (Phase 3):** JWT auth with refresh-token rotation + reuse detection (family
  revocation); roles + policies (Customer / OrganizerStaff / PlatformAdmin); the signed
  `tenant_id` claim replaced the `X-Tenant-Id` header.
- **Done (Phase 4):** resilient payment client (`IHttpClientFactory` + resilience pipeline,
  idempotency keys, typed failures) tested against WireMock; Redis cache-aside with per-tenant
  keys, jittered TTLs, and write-path invalidation.
- **Done (Phase 5, tag `v2-eventdriven`):** the centerpiece — **oversell prevention implemented
  three ways** (optimistic `xmin` + retry [active default], pessimistic `SELECT ... FOR UPDATE`,
  Redis atomic decrement) behind a config-switched `IReservationStrategy`, proven by parallel-
  buyer tests; the **booking saga** (hold → charge → confirm, decline keeps the hold alive for
  retry, expiry is the compensation); **transactional outbox → RabbitMQ → idempotent consumer**
  with a dead-letter exchange; hold-expiry background service. 101 tests against real Postgres,
  Redis, and RabbitMQ containers.
- **Done (Phase 6):** multi-stage non-root Dockerfile + one-command full-stack compose; health
  probes; GitHub Actions CI (all tests + GHCR image); Kubernetes manifests (2 API replicas,
  readiness/liveness, resources); per-IP auth rate limiting; graceful shutdown; OpenTelemetry
  traces + metrics with trace propagation across the RabbitMQ hop.
- **Done (Phase 7):** SignalR live availability + Redis backplane; CQRS availability read model;
  async ticket-PDF issuing (QuestPDF, fan-out consumer, file-storage port); the event sales
  report as a vertical slice; the architecture write-ups in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
  (Clean vs Vertical Slice, monolith vs microservices, every key decision with its trade-off).

The full architecture rationale — the interview-ready version — lives in
**[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**.
