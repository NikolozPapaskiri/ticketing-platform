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

```bash
# 1. PostgreSQL (host port 5433 — 5432 is often taken by a native install)
docker compose up -d

# 2. Build
dotnet build TicketingPlatform.sln -c Release

# 3. Apply migrations (cross-project: migrations live in Infrastructure, startup is Api)
dotnet ef database update --project src/TicketingPlatform.Infrastructure --startup-project src/TicketingPlatform.Api

# 4. Run
dotnet run --project src/TicketingPlatform.Api
```

API: `http://localhost:5000`, all routes under `/api/v1`. OpenAPI document at
`/openapi/v1.json` in Development (document name `v1` is unrelated to the API version). `GET /`
returns 404 by design — this is an API, not a site.

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
| POST | `/api/v1/holds` | reserve inventory under a 10-min TTL; insufficient stock → 409 |
| GET | `/api/v1/holds/{id}` | inspect a hold |
| POST | `/api/v1/holds/{id}/release` | give the quantity back; double release → 409 |

All event and hold routes require `X-Tenant-Id` (400 without it). All errors are RFC 7807.

## Roadmap (staged; tags mark milestones)

- **Done (Phase 2, tag `v2-clean`):** Clean Architecture (use-case services + repository ports,
  thin controllers, Api free of EF), integration tests with Testcontainers against real
  Postgres (79 tests total), and the `Hold` TTL reservation concept.
- **Now (Phase 3):** authentication & authorization in-repo — Identity + JWT + refresh tokens;
  roles, policies, resource-based authorization; the tenant claim replaces the header.
- **Phase 4:** resilient payment-provider client (`IHttpClientFactory` + Polly), Redis
  cache-aside, CQRS read models.
- **Phase 5:** the centerpiece — oversell prevention compared three ways (optimistic `xmin`,
  pessimistic locking, Redis atomic decrement), RabbitMQ with a transactional outbox, the booking
  saga (hold → pay → confirm → issue), background services (hold expiry, reconciliation), ticket
  PDFs in object storage. Tag `v2-eventdriven`.
- **Phase 5b:** SignalR live availability (Redis backplane).
- **Phase 6:** Dockerfile + full-stack compose, GitHub Actions CI, Kubernetes (probes, multiple
  replicas), rate limiting, graceful shutdown, OpenTelemetry. Tag `v3-production`.
- **Phase 7:** vertical-slice set piece + architecture write-ups (Clean vs Vertical Slice,
  monolith vs microservices).
