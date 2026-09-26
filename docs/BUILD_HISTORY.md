# Build history

Moved from the STATUS section of `CLAUDE.md` on 2026-09-26. This is a historical record and is not maintained: several items below that read as "open", "next", or "unmerged" have since closed, and the test counts are snapshots. For current state see `docs/ROADMAP.md`.


- **Done — Phase 1, tagged `v1-naive` (pushed):** naive single-project ASP.NET Core 10 + EF Core 10 + PostgreSQL API, compiling and running on .NET 10.
  - Multi-tenancy via EF Core global query filter, verified end to end (tenant B gets 404 on tenant A's event; missing `X-Tenant-Id` → 400).
  - Entities Tenant/Event/TicketType/Inventory; `InitialCreate` migration in source control; `Inventory` uses the Postgres `xmin` shadow property as an optimistic-concurrency token (Npgsql 10 removed `UseXminAsConcurrencyToken()`).
  - Guarded Event state machine on the entity (`CanTransitionTo` / `TransitionTo`, explicit transition table Draft → OnSale → Closed, Closed terminal). `POST /api/events/{id}/publish` and `/close` return **409 ProblemDetails** on illegal moves; the entity throws `InvalidStatusTransitionException` as a backstop.
  - Paged + filtered browse on `GET /api/events` (`page`/`pageSize`/`status`, page validated, pageSize clamped 1–100, stable `OrderBy(StartsAt).ThenBy(Id)`, count-then-page).
  - Uniform RFC 7807 error contract (every error carries `type` + `traceId` via the `Problem()` helper). Correlation-id middleware.
- **Done — Phase 2 so far (all committed and pushed):**
  - `tests/TicketingPlatform.UnitTests` (xUnit 2.9.3): **41 green tests** — full state-machine transition matrix (`[Theory]`/`[InlineData]`, 9+3+6 cases) + all three FluentValidation validators (regex boundaries for currency and slug, price/quantity bounds, `FakeTimeProvider` for the future-date rule).
  - FluentValidation in Application; validators resolved per-request; **`FluentValidationFilter`** (global `IAsyncActionFilter`) validates any action argument with a registered `IValidator<T>` and short-circuits with RFC 7807 `ValidationProblem` — controllers contain no validation code.
  - API versioning (`Asp.Versioning.Mvc` 10): URL-segment `api/v{version}/...`, default v1.0, `ReportApiVersions`. `requests.http` updated to `/api/v1`.
  - **Clean Architecture refactor COMPLETE (all 5 stages):** solution split into `Domain` (entities + state machine, zero deps) ← `Application` (contracts, validators, `Result`/`Result<T>` in `Common/`, ports `ITenantContext`/`ITenantRepository`/`IEventRepository` in `Abstractions/`, use-case services `TenantService`/`EventService` in `Services/`) ← `Infrastructure` (`Persistence/TicketingDbContext` + migrations + `Repositories/` + `AddInfrastructure(connString)`) ← `Api` (thin controllers, middleware, filter, composition root). **Api has zero EF usage** (grep-verified); controllers keep only HTTP concerns (tenant guard, page validation, Result→status mapping). Services report expected failures as Results (NotFound/Conflict), never exceptions. EF verified cross-project; `has-pending-model-changes` = none; full runtime smoke green (create/graph/transitions 204+409/pagination/cross-tenant 404 incl. cross-tenant transition).
  - Security: `System.Security.Cryptography.Xml` pinned to 10.0.9; `Microsoft.OpenApi` pinned to 2.7.5 (both NU1903, transitive). Gotcha learned: `Microsoft.EntityFrameworkCore.Design` must stay on the **startup** project (Api) for EF tools — design-time only (`PrivateAssets`), so it does not violate the dependency rule.
- **Done — Phase 2 COMPLETE, tagged `v2-clean`:**
  - Integration tests (`tests/TicketingPlatform.IntegrationTests`): `WebApplicationFactory` + **Testcontainers** (throwaway postgres:17, real migrations, one container per run via collection fixture). 21 tests: tenant isolation machine-verified (cross-tenant read AND write → 404, list scoping, missing header 400 with RFC 7807 body), state machine 204/409/404, pagination totals + status filter, duplicate slug 409, validation field errors, full create → browse → get flow. `Program` exposed via `public partial class Program {}`.
  - **Hold concept** (TTL reservation, single-threaded correctness): `Hold` entity (Active/Confirmed/Released/Expired, guarded moves), reservation math on `Inventory` (`TryReserve` rejects overdraw, `Release` clamps at capacity), `HoldService` (decrement + hold row in ONE transaction; TTL 10 min via injected `TimeProvider`; insufficient stock → 409 with live availability), `IHoldRepository` port + EF impl, tenant-scoped, `(Status, ExpiresAt)` index for the Phase 5 expiry scanner. Endpoints: `POST /api/v1/holds`, `GET /api/v1/holds/{id}`, `POST /api/v1/holds/{id}/release`. `AddHolds` migration.
  - **Test count: 79 (58 unit + 21 integration), all green.**
- **Done — Phase 3: Authentication & Authorization (all committed):**
  - Custom user store (`User` w/ `UserRole` Customer/OrganizerStaff/PlatformAdmin; staff carry `TenantId`) + **PBKDF2 via Identity's `PasswordHasher`** behind an `IPasswordHasherService` port. Users deliberately NOT tenant-filtered (login precedes tenant; documented in DbContext).
  - **JWT bearer**: HMAC-SHA256, 15-min access tokens (`sub`/`email`/`role`/`tenant_id` claims, `MapInboundClaims=false`, 30s clock skew); **refresh tokens stored as SHA-256 hashes, 7-day, with rotation + reuse detection** (replaying a rotated token revokes the whole family). `JwtOptions` bound from config; dev signing key in appsettings.Development.json (labeled), prod via env.
  - **`X-Tenant-Id` header is GONE**: `TenantResolutionMiddleware` reads the signed `tenant_id` claim from the principal (pipeline: authN → tenant resolution → authZ). Clients can no longer choose their tenant.
  - Policies: `OrganizerStaff` (role AND tenant claim) on events/holds; `PlatformAdmin` role on tenants + staff provisioning. Self-registration is always Customer; staff/admin accounts are admin-provisioned. Dev seeds `admin@platform.local`/`Admin123$` (DEV ONLY) + auto-migrates in Development only.
  - Endpoints: `POST /auth/register`, `/auth/register-staff` (admin), `/auth/login`, `/auth/refresh`.
  - **87 tests green (58 unit + 29 integration)** — authz matrix (401/403/404), token rotation + family revocation, isolation via staff tokens. `requests.http` rewritten for the auth flow.
  - Deferred (documented): resource-based authorization handler arrives with Phase 5 orders ("customer sees own order") — tenant isolation is already enforced by the query filter; rate limiting on /login and /token lands in Phase 6 with the rate-limiting middleware.
- **Done — Phase 4 (resilience + Redis):**
  - `IPaymentGateway` port + typed `HttpClient` via `IHttpClientFactory` with `Microsoft.Extensions.Http.Resilience` standard pipeline (retry + backoff + jitter, circuit breaker, timeouts). Idempotency-Key per charge makes retries safe; 4xx declines never retried; outages return typed `ProviderUnavailable` (→ 503), never exceptions. Failure paths tested with WireMock.Net (retry-to-success proven, decline single-call proven). Retry base delay configurable (`PaymentProvider:RetryBaseDelayMs`).
  - `ICacheService` port + `RedisCacheService` (jittered TTLs vs stampedes, degrade-to-DB when Redis is down). Event graph cached per tenant (`CacheKeys.EventGraph` — **tenant-prefixed keys** prevent cross-tenant leaks through the shared cache). Invalidation on transitions, ticket-type adds, hold create/release, AND hold expiry (read-your-writes everywhere). Cache-hit/invalidation/tenant-isolation proven against a real Redis container.
- **Done — Phase 5, tagged `v2-eventdriven`:**
  - **Oversell prevention, all three ways**, behind pluggable `IReservationStrategy` selected by `Reservation:Strategy` config: `OptimisticConcurrency` (DEFAULT — xmin token + reload-retry loop), `PessimisticLock` (raw `SELECT ... FOR UPDATE` via ADO inside the EF transaction; EF composes filters around raw SQL and Postgres rejects FOR UPDATE in the wrapper, hence hand-written tenant predicate), `RedisAtomic` (DECRBY gate, SET NX seeding, compensating INCRBY, documented drift window). Concurrency tests: 30 parallel buyers vs 10 tickets → zero oversell, zero 500s, books balance exactly.
  - **Booking saga**: `Order` aggregate (PendingPayment → Confirmed/PaymentFailed), `OrderService` = hold validation → charge (order id = idempotency key) → confirm hold + order + **outbox event in ONE transaction**. Declined → hold stays Active for retry until TTL (compensation = expiry). Provider down → 503, nothing persisted.
  - **Transactional outbox + RabbitMQ**: `IOutbox`/`OutboxWriter` (stages via the caller's scoped DbContext = same transaction), `OutboxDispatcher` BackgroundService (polls, publishes to topic exchange `ticketing-events`, routing key = event type, MessageId = outbox id, at-least-once), `NotificationConsumer` (idempotent via `ProcessedMessages` dedupe table checked+written in one transaction; poison messages nack'd without requeue → DLX `ticketing-dlx`), `HoldExpiryService` (IgnoreQueryFilters — background scope has no tenant; safe under replicas because the state machine + xmin guard it; emits `HoldExpired` via outbox).
  - RabbitMQ creds: `ticketing/ticketing` everywhere (the built-in `guest` user is loopback-only, and Docker port-proxied connections do not qualify — this burned an hour, it is a real gotcha).
  - Endpoints: `POST /api/v1/orders` (201/404/409/503), `GET /api/v1/orders/{id}`. `AddHolds`, `AddAuth`, `AddOrdersAndMessaging` migrations. Configurable `Holds:TtlSeconds` + `Holds:ExpiryScanSeconds`.
  - **101 tests green (58 unit + 43 integration)** incl. the full saga chain (order → outbox → broker → consumer → notification, polled), decline-then-retry on the same hold, expiry compensation on a dedicated short-TTL container set.
- **Deferred (recorded honestly, planned for the Phase 6/7 window):** CQRS read models (pairs naturally with SignalR availability push), SignalR live availability, ticket PDF/object storage, resource-based authorization handler (tenant isolation is enforced by query filters; the handler becomes meaningful with customer-owned orders), rate limiting on auth endpoints.
- **Done — Phase 6: containerization, CI/CD, Kubernetes, observability:**
  - Health probes: `/health/live` (deliberately dependency-free) + `/health/ready` (EF check for Postgres, custom cached-connection checks for Redis PING + RabbitMQ). Probes anonymous; tests pin that.
  - Per-IP fixed-window **rate limiting** on `/auth/*` (429 before PBKDF2 runs); `RateLimiting:AuthRequestsPerMinute` (main test factory raises it; a dedicated tight-limit factory proves the 429).
  - **OpenTelemetry** traces (AspNetCore, HttpClient, `Npgsql` source, `TicketingPlatform.Messaging` source) + metrics (AspNetCore, HttpClient, runtime); OTLP export when `Otlp:Endpoint` set. **Cross-queue trace propagation**: outbox rows store `Activity.Current.Id` (W3C traceparent, `AddOutboxTraceParent` migration), dispatcher opens a Producer span + stamps the `traceparent` header, consumer rejoins as a Consumer span — one trace: HTTP → outbox → broker → consumer.
  - Graceful shutdown: `HostOptions.ShutdownTimeout` 30s (in-flight sagas drain on SIGTERM).
  - **Multi-stage Dockerfile** (csproj-first layer caching, aspnet runtime image, non-root `$APP_UID`, port 8080) + `.dockerignore`; **api service in docker-compose** → `docker compose up -d --build` boots the whole product (verified: containerized API migrated, seeded, readiness 200, login OK). Compose env `Development` = migrate+seed (documented; real prod migrates in a pipeline step).
  - **GitHub Actions CI** (`.github/workflows/ci.yml`): restore/build/test (Testcontainers runs on ubuntu runners) + docker build, GHCR push from `main`.
  - **k8s/** Kustomize manifests: namespace, postgres/redis/rabbitmq (dev-cluster-only: emptyDir, single replica), ConfigMap + Secret split, API Deployment ×2 replicas with readiness/liveness probes + resource requests/limits, ClusterIP service. `kubectl kustomize` render validated (11 objects). Run: build image `ticketing-api:local` → `kind load` → `kubectl apply -k k8s/`.
  - **105 tests green (58 unit + 47 integration).**
- **Done — Phase 7 (final), tagged `v3-production`:**
  - **SignalR** `AvailabilityHub` (`/hubs/availability`, per-event groups, anonymous) + **Redis backplane** so broadcasts cross replicas; `IAvailabilityBroadcaster` port keeps SignalR out of Infrastructure. MessagePack pinned to 3.1.8 (the backplane's default 2.5.x carried NU1902/03 advisories).
  - **CQRS availability read model**: `AvailabilityChanged` staged in the same transaction as every availability write (hold create/release/expiry, ids-only so the projection re-reads live truth = idempotent/self-healing); `AvailabilityProjectionConsumer` maintains `EventAvailabilityView`; `GET /events/{id}/availability` serves it off the contested write path.
  - **Async ticket PDF**: `ITicketDocumentGenerator` (QuestPDF) + `IFileStorage` (LocalFileStorage, path-traversal guard) ports; `TicketIssuerConsumer` is a second `OrderConfirmed` consumer (topic fan-out, own queue/dedupe); `GET /orders/{id}/ticket` streams it (404 until issued). Dedupe table reworked to a per-consumer composite key `(MessageId, Consumer)`.
  - **Vertical slice**: `Api/Features/SalesReport/GetEventSalesReport.cs` (minimal API, one file, reaches DbContext directly - the one deliberate breach; file header IS the Clean-vs-Slice argument). Project-then-group-in-memory because grouped aggregates through navigations are an EF translation gap.
  - **Write-ups**: `docs/ARCHITECTURE.md` (Clean vs Vertical Slice, monolith vs microservices with the "seams already exist via outbox+broker" argument, every key decision + trade-off). README updated.
  - `AddAvailabilityReadModel` + `AddTicketsAndPerConsumerDedupe` migrations. **110 tests green (58 unit + 52 integration).**
- **PROJECT COMPLETE.** Tags: `v1-naive` → `v2-clean` → `v2-eventdriven` → `v3-production`. Remaining is the user's own work: the W14 mock-interview reps against this codebase (drive `requests.http`, defend each layer out loud, break things on purpose). Optional future depth (scoped as design write-ups, not required): reserved-seating map, Elasticsearch search, virtual waiting room / queue-based load leveling.
- **Note:** the repo also carries `AGENTS.md` (a Codex-facing mirror of this plan, maintained by the user); keep its STATUS in sync with this file if both agents are used.
- **Historical roadmap note:** the backend production path listed here is complete; use Latest status below for current work.
- **Decision (recorded 2026-07):** the platform stays **self-contained** — no external auth server or third-party project integration; everything is built in this repository per the original plan.

## Latest status - 2026-07-11

This block supersedes older phase-progress lines above if they disagree.

- Backend milestones are complete through Phase 7 / `v3-production`.
- Post-v3 product hardening is complete: customer public catalog, customer holds/orders,
  refunds, ticket validation codes, order idempotency, ownership checks, domain metrics,
  multi-replica outbox claiming, and shared ticket-file storage config for Docker/Kubernetes.
- Frontend milestones M0-M5 are complete in `apps/web`: public storefront, customer
  checkout/account, organizer portal, admin portal, Next.js BFF with HttpOnly cookies, SignalR
  client, Playwright golden journey, CI web job, docker-compose `web` service, and the
  tkt.ge-style marketplace (global catalog, categories, images, search, date filters).
- **Virtual waiting room (queue-based load leveling) is implemented** end to end:
  `Event.WaitingRoomEnabled` (organizer checkbox, `AddWaitingRoom` migration), Redis sorted-set
  line + TTL'd admission keys (`RedisWaitingRoom`, replica-safe via atomic ZPOPMIN),
  `WaitingRoomAdmitter` background valve (`WaitingRoom` config section: batch 5 / 5s / 300s TTL),
  anonymous `POST/GET /public/events/{id}/queue` endpoints, enforcement at
  `POST /customer/holds` (`X-Visitor-Id` header, 429 when not admitted; staff/box-office bypass
  by design), SignalR `queueAdmitted`/`queuePosition` pushes on per-visitor groups with a poll
  fallback, and the `WaitingRoomGate` web component (visitor id in localStorage).
- **Durable payment state machine (hardening plan PR 1) is done.** Checkout now: replay/recover
  by idempotency key → atomically claim `Active → PaymentPending` (hold `xmin` token) + open a
  `PendingPayment` order + record the key in ONE transaction committed BEFORE the charge (order
  id = stable provider key) → charge with no DB txn open → finalize (Confirmed / PaymentFailed +
  hold back to Active-or-Expired / ambiguous stays PendingPayment → **202**). A PaymentPending
  hold is never expiry-reclaimed; a partial unique index enforces one live order per hold; Order
  `xmin` blocks double-finalize; `IPaymentGateway.GetChargeStatusAsync` + `PaymentReconciliation
  Service` settle orphaned leases (multi-replica-safe via the tokens). `AddDurablePaymentState`
  migration. Deterministic race harness in tests (`AsyncGate`, `ControllablePaymentGateway`,
  `FaultInterceptor`). **Follow-on plan PRs: PR 2 (atomic refund/scan/release), PR 3 (RabbitMQ
  publisher confirms + topology), PR 4 (waiting-room Lua/token-bucket), PR 5 (session safety) — all
  done; PR 6 (prod ops) remains.**
- **Atomic post-payment transitions (hardening plan PR 2) is done.** Refund claims
  `Confirmed → RefundPending` (order `xmin`) with a stable `refund:{orderId}` key so customer +
  staff can't double-refund; ticket scan is an `xmin` compare-and-swap (`Issued → Scanned`, one
  admission); hold release credits inventory exactly once under a race (optimistic no longer
  re-credits on a hold-row conflict; pessimistic/redis roll back idempotently). **Policy: a
  scanned ticket is non-refundable** (409). `AddRefundPendingAndTicketConcurrency` migration.
  Next: PR 3 (RabbitMQ
  publisher confirms + topology + bounded retry).
- **RabbitMQ delivery safety (hardening plan PR 3) — DONE.** A `RabbitMqTopologyInitializer`
  declares the exchange/DLX/all consumer queues+bindings+retry queues before the dispatcher starts;
  the dispatcher publishes with **publisher confirms + tracking** and `mandatory: true`, marking a
  row processed only after the broker ACKs, with exponential-backoff retry (`NextAttemptAt`) and
  operator-visible quarantine (`FailedAt`/`LastError`). Consumers share one failure policy (poison
  → DLQ immediately; transient → durable per-consumer/per-event TTL retry queue with an attempt
  cap). Events cross the boundary as typed `IIntegrationEvent` records wrapped in a **versioned
  envelope** (messageId/eventType/schemaVersion/occurredAt/tenantId/correlationId/payload). Fixed a
  latent bug: `OrderRefunded` had no binding and was silently dropped. Full §3.5 test set +
  delivery metrics (outbox backlog-age gauge, returned/retried/quarantined counters, confirm-latency
  histogram, consumer retry/DLQ counters). Fixed `OrderRefunded` routing. Next: **PR 4** (waiting
  room atomicity + global admission control).
- **Waiting-room safety (hardening plan PR 4) — DONE.** `AdmitBatchAsync` is one atomic Lua
  script (pop + grant + positions + empty-line de-register — no pop-before-grant crash window)
  metered by a per-event Redis **token bucket** (`AdmitRatePerSecond` + `AdmitBurst`), so the
  global rate is replica-count independent. An admission is a Redis hash grant (quota + bound
  customer, TTL'd): `TryConsumeAdmissionAsync` atomically verifies it for the event, binds it to
  the authenticated customer on first use (leaked GUID → 403), and decrements the hold quota
  (exhausted → 429). Anonymous joins are per-client (IP) fixed-window throttled (→ 429). §4.5 gate
  met (atomic, rate replica-independent, leaked GUID insufficient, abuse bounded, poll+SignalR ok).
- **Session safety (hardening plan PR 5) — DONE.** Refresh tokens are **family-scoped** (one login
  = one family). Rotation is an atomic CAS — `TryRotateAsync` runs a conditional
  `UPDATE ... WHERE RevokedAt IS NULL` and inserts the successor in the same tx, so parallel
  refreshes can't fork the session or mint two successors. A rotated-token replay **within** a
  configurable grace window (`Auth:RefreshRotationGraceSeconds`, default 5s) is a legitimate
  concurrent refresh (sibling in the same family); **outside** it revokes only that family (not the
  user's other devices). Reads are `AsNoTracking` so the post-claim re-read is fresh (fixed the
  stale-identity-map bug the concurrency test caught). Server-side logout (`POST /auth/logout`
  revokes the family; the BFF calls it before clearing cookies) + BFF per-replica single-flight.
  **Proxy-aware rate limiting**: `UseForwardedHeaders` honours `X-Forwarded-For` only from
  configured trusted proxies (`ReverseProxy` section), else ignores it. Startup validation
  (`SecurityOptionsValidation`) rejects a missing/short/`DEV-ONLY` JWT key. Migration
  `AddRefreshTokenFamily`.
- **Ops hardening (hardening plan PR 6) — DONE.** One image, three profiles via `Hosting:Role`
  (`All`/`Api`/`Worker`): the 8 background workers register only on a worker-running role, so scaling
  HTTP replicas no longer multiplies the admission valve or scheduled scans. Readiness is role-aware
  — Postgres/Redis gate everywhere, **RabbitMQ is async for the API** (broker outage buffers the
  outbox rather than dropping API pods) and hard only for a worker; `/health/detail` is non-gating.
  Shared object storage (`S3FileStorage`/MinIO behind `IFileStorage`, `FileStorage:Provider`)
  replaces the multi-replica-hostile RWO ticket-files PVC; idempotent/atomic writes; SDK-v4 checksum
  disabled for MinIO. New metrics (scan conflicts, admission rate + queue depth, payment/refund
  reconciliation backlog, hold-expiry lag) + a worker-only `RetentionService` pruning
  outbox/dedupe/idempotency/dead-refresh-token rows on configurable windows. CI runs Playwright vs the
  real compose stack, builds+scans both Dockerfiles (Trivy), audits NuGet/npm, gates on EF model
  parity; Dependabot enabled. Migration-free (no schema change).
- Current verification: 197 backend tests (78 unit + 119 integration, incl. 7 host-role/retention: 3
  host-role + 3 S3-storage + 1 retention, 7 session/proxy, 14 waiting-room, 6
  payment-race/reconciliation, 5 refund/scan/release across all strategies, and the messaging suite:
  unroutable/backoff/quarantine/broker-disconnect/crash-redeliver/versioned-envelope/consumer-retry/
  poison/topology-readiness/duplicate-ticket), plus frontend typecheck, lint, production build,
  Playwright e2e (4), and live smoke.
- Current run targets: web UI `http://localhost:3000`, API `http://localhost:5000`, OpenAPI JSON
  `http://localhost:5000/openapi/v1.json`. API `GET /` returns 404 by design.
- Use `localhost`, not `127.0.0.1`, for Next dev and Playwright. Local HTTP auth cookies need
  `COOKIE_SECURE=false`; production HTTPS should keep secure cookies enabled.
- **Flash-sale load test done** (`tools/TicketingPlatform.LoadTest`, results + analysis in
  `docs/LOAD_TEST.md`): 100 workers vs 300 tickets per strategy — all three sold exactly
  300/300 with zero oversell. Optimistic = 86% wasted attempts under contention; Pessimistic =
  zero waste but p99 ~2s lock queue; RedisAtomic = ~1,900 attempts/s absorbed, losers rejected
  in ~13ms without touching Postgres. The test also caught and fixed a real bug: RedisAtomic
  winners fought each other's xmin token on the DB mirror write (6k+ 500s) — now a single
  atomic `ExecuteUpdate` in the same transaction as the hold insert.
- The production safety hardening plan (**PR 1-6**) is **IMPLEMENTED and pushed** (branches
  unmerged). **Distributed login limiter: DONE** (`IDistributedRateLimiter` + Redis fixed window +
  `DistributedAuthRateLimitFilter`; per-replica window kept as the fail-open backstop). Remaining
  deferred follow-up noted in the plan: an
  HMAC-signed join-token + join challenge for waiting-room queue integrity. Reserved seating and
  Elasticsearch remain paused.
- **PR 6 §6.5 CI** (only YAML-validated at authoring) had two real failures on first run, **now
  fixed**: (1) `images` pinned `trivy-action@0.28.0` (nonexistent tag) → `0.35.0`; (2) `e2e` crashed
  because the container API's relative `DataProtection:KeysPath` (`.aspnet/...` from
  appsettings.Development) resolved under root-owned `/app` and the non-root user couldn't create it →
  point it at the writable `/var/ticketing/keys` via a Dockerfile env. Reproduced + verified locally:
  the full compose stack boots and all 4 golden-journey tests pass (incl. the S3/MinIO ticket
  download). Still open (non-fatal): Node-20 deprecation warnings on the actions.
- **Observability** (`docs/OBSERVABILITY_PLAN.md`): app exports metrics + traces + **logs** over
  OTLP (role-aware service name). `docker-compose.observability.yml` overlays OTel Collector ->
  Prometheus/Loki/Tempo -> **Grafana** (provisioned datasources + "Ticketing — Overview" dashboard),
  plus postgres/redis/RabbitMQ/MinIO exporters. In-app **`/admin/ops`** page + `GET /api/v1/admin/ops`
  (PlatformAdmin) render a source-of-truth snapshot (health + backlogs), accurate in any topology
  since it doesn't read the worker-populated gauges. 200 tests (78 unit + 122 integration).
  **P1-P5 are DONE and verified against a live stack** (alert rules + Alertmanager, 5 dashboards,
  `k8s/monitoring/` overlay, all 6 Prometheus targets up, Loki streams carry TraceId/SpanId, Tempo
  holds traces). Metric names had to be corrected to the collector's OTel-conventional forms, and a
  second `rabbitmq-detailed` scrape job was needed for per-queue depth.

## Next plans — 2026-07-27

- **`docs/ROADMAP.md`** is now the single source for what is next. It supersedes the roadmap
  sections of `docs/TICKETING_PLATFORM_PRODUCT_RESEARCH.md` (written before PRs 3-6 landed and
  stale on several items). It was written by re-verifying claims against the source, not the status
  blocks. Structure: Track 1 = Gate 0 safety close-out (5 verified-open items, ~1 PR), Track 2 =
  product Phases A-F (venue/performance/reserved seating first), Track 3 = multi-tenancy maturity,
  Track 4 = platform/ops leftovers, plus a "what not to build" section.
- **`docs/MULTI_TENANCY.md`** is the general-solution write-up for this class of product: the four
  problems every ticketing platform is made of, the canonical `Venue/Hall/SeatMap` +
  `Event/Performance/PriceZone/Allocation` model, the six invariants, and a multi-tenancy deep-dive.
  Its central claim: ticketing is B2B2C, so it has **two tenancy planes** (organizer = one tenant,
  fails closed on tenant mismatch; customer = no tenant, fails closed on ownership), which is why
  ~a third of read paths legitimately need `IgnoreQueryFilters`. Also covers isolation-strategy
  trigger conditions, the ticketing-specific noisy-neighbour problem (scheduled spikes, cells), the
  money model as a tenancy decision, per-tenant settings, and tenant lifecycle.
- **Verified still-open Gate 0 items** (checked in code, not assumed): no
  `IPaymentGateway.GetRefundStatusAsync`; `OrderService.FinalizeAsync:154` extends the payment lease
  with plain `SaveChangesAsync` (the confirmed path at `:168` correctly uses `TrySaveChangesAsync`);
  `Order` has no refund-initiator field; `Order.RevertRefundClaim()` leaves `RefundClaimedAt` set;
  the payment/refund reconcilers lack the `FOR UPDATE SKIP LOCKED` claim the outbox dispatcher has.
- **Already closed despite older docs saying otherwise:** bounded outbox retries + quarantine
  (`AddOutboxRetrySchedule`), versioned envelopes (`AddIntegrationEventEnvelopeMetadata`), broker
  failure-window tests, observability P5, distributed login limiter.
