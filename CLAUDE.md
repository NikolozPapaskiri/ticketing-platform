# CLAUDE.md — project context and learning plan

This file is the durable context for this repository. Claude Code reads it at the start of every session. It defines who I am, how you (Claude Code) should work with me, the full learning plan, what each phase produces, and where we are right now.

---

## Who I am and what I am doing

I am a Lead OutSystems developer (5+ years, banking domain: after-sales loan servicing, async prepayment flows, REST APIs, normalized schemas, Kong gateway, C# extensions with MimeKit). I already hold senior engineering judgment. I am reactivating and deepening .NET to interview and work as a mid-to-senior .NET backend engineer, and to be able to rewrite production systems in .NET.

This repo is my flagship learning project: a **multi-tenant event ticketing and booking platform** (a working "design Ticketmaster"). It is one system that I grow across all phases. The project was chosen to teach the senior topics my banking work did not exercise (hard concurrency under contention, surge load leveling, real-time push, multi-tenancy, CQRS) while reusing patterns I already know (sagas, idempotency, messaging, state machines).

**Target framework:** .NET 10 (LTS), C# 14, ASP.NET Core 10, EF Core 10. .NET 10 shipped November 11 2025 and is supported until November 2028; .NET 8 (prior LTS) is supported only until November 2026, so we build on .NET 10. Sources: https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/ and https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core. Day-to-day reference: https://learn.microsoft.com/en-us/dotnet/.

---

## How you (Claude Code) should work with me

1. **Treat me as a senior engineer reactivating .NET, not a beginner.** Move fast through concepts I already own (REST/HTTP semantics, idempotency, relational modeling, async and messaging concepts, caching as a concept, code review, leading delivery). Spend the time on .NET-specific mechanics, idioms, and the things interviewers probe.

2. **Teach, do not just autocomplete.** For every new topic, give me four things: why it exists, the internals (what actually happens under the hood), the common mistakes, and the interview questions it seeds. When you write code, explain the non-obvious .NET-specific choices, not the syntax.

3. **Verify by building.** You have a terminal. Run `dotnet build`, `dotnet ef`, and the test suite. Fix failures before handing code back. Never give me code you have not compiled and run. This feedback loop is the whole reason I moved from the chat to you.

4. **Enforce the milestone gates.** Do not advance to the next phase until I can demo the current phase's milestone and answer its interview questions out loud. If I try to skip ahead, push back and tell me what is unfinished. Slow is fine; skipping foundations is not.

5. **Make me do the reps.** For interview-critical areas (async internals, `IEnumerable` vs `IQueryable`, EF change tracking and N+1, the authorization model, the concurrency strategies), have me explain the concept back or implement the core myself before you fill in around it. Do not let me passively read.

6. **Always surface the tradeoff and the "when NOT to."** For every pattern, tell me where it is the wrong choice. Arguing against over-engineering (when not to cache, when not to split into services, when Clean Architecture is overkill) is a senior signal I need to be able to give.

7. **Keep Git discipline.** Short-lived `feature/*` branches, Conventional Commits (`feat:`, `fix:`, `refactor:`, `test:`), small PRs, self-review the diff before merge, tag the version milestones (`v1-naive`, `v2-eventdriven`, `v3-production`).

8. **Track progress.** When a phase or milestone completes, update the Status section at the bottom of this file so the next session knows where we are.

9. **My working preferences.** Be direct and first-principles. Cite sources for factual claims. No em dashes, no filler, no clichés. Usefulness over politeness. If you make a mistake, say so plainly and fix it.

**What I will give you each session:** which phase and week I am on, any build or test output, the specific thing I am stuck on or want to learn next. Default to reading this file first, then ask what I want to tackle if it is not obvious.

---

## The project and its architecture progression

A B2B2C platform. Event organizers (tenants) create events and sell tickets; customers browse and buy. The hard part is not CRUD: it is selling a finite, contested resource correctly, at a traffic spike, in real time, for many tenants at once.

The point of the progression is that I **feel the need before adopting the pattern**:

1. **Phase 1:** one project, controllers calling `DbContext` directly. Deliberately un-layered, with multi-tenancy baked in. I learn ASP.NET Core and EF Core without architecture noise.
2. **Phase 2:** once the naive version hurts (testing is awkward, rules leak into controllers), refactor to **Clean Architecture** (Domain / Application / Infrastructure / Api). Now I can explain why it exists from experience.
3. **Phase 7:** implement one feature as a **Vertical Slice** and write up the tradeoff against Clean Architecture.
4. **Throughout:** it stays a **modular monolith**. In Phase 7 I write the honest case for not splitting it into microservices.

### Required tech coverage

| Required tech | Where it appears |
|---|---|
| ASP.NET Core Web API | The whole API surface |
| Multi-tenancy | Organizer tenants, isolation, tenant-scoped authz, per-tenant config |
| AuthN / AuthZ (JWT, roles, claims, policies, resource-based) | Platform admin vs organizer staff (tenant-scoped) vs customer (owns own orders) |
| EF Core + DB design | Tenants, events, ticket types, inventory, holds, orders, payments, tickets |
| Validation | Event dates, money rules, capacity, guarded status transitions |
| Logging / error handling | Structured logs, correlation IDs, RFC 7807 error contract |
| External API integration | Mock payment provider (resilient, idempotent) |
| Redis caching | Hot read paths (event details, availability), distributed locks, SignalR backplane |
| RabbitMQ messaging | `TicketsSold`, `OrderConfirmed`, `PaymentFailed`; outbox; dead-letter |
| Background services | Hold-expiry release, settlement reconciliation, scheduled jobs |
| File storage | Event images and ticket PDFs (local then MinIO/S3) |
| Docker / CI/CD / K8s | Containerized stack, GitHub Actions, kind/minikube, multiple replicas |
| Monitoring / prod readiness | Health checks, metrics, tracing, graceful shutdown, rate limiting |
| **Net-new senior topics** | High-contention concurrency / oversell prevention, queue-based load leveling (virtual waiting room), real-time push (SignalR), CQRS read models |

### Core path vs stretch

Build the **core path** to be senior-ready; treat **stretch** as extra depth or convert it to a design write-up if time is short.

- **Core:** multi-tenant CRUD with tenancy filters, auth and multi-tenant authz and audit, capacity-based inventory, holds with TTL, oversell prevention (all three strategies compared), the booking saga with outbox and RabbitMQ, payment integration with resilience, Redis caching, CQRS read models for browse, SignalR live availability, ticket PDF and object storage, Docker, CI/CD, Kubernetes (multi-replica), observability.
- **Stretch:** virtual waiting room / load leveling, reserved seating with a seat map, search at scale (Elasticsearch), refunds and partial cancellations, per-tenant rate limiting, blue/green deploy, schema-per-tenant isolation as a real implementation.

One finished core beats a half-built max.

---

## Time and cadence (honest)

The plan is structured as 8 phases over a nominal 14 "weeks" of content. The hour blocks below (for example "Mon–Tue ~5h") are **effort estimates, not calendar mandates**. At a realistic ~15h/week, each content-week takes roughly 1.5 calendar weeks, so plan on about 5 months to solid mid-level and about 8 to 10 months to senior-interview-ready, because this project's net-new surface (multi-tenancy, hard concurrency, real-time, CQRS, surge) is larger than a standard CRUD project. The **weekly milestone is the real gate**: advance when I can demo it, not when the calendar says so.

Sequencing, time estimates, project design, and pedagogical choices are engineering judgment, not citable facts. Framework version facts are cited above.

---

# Phase 0 — Environment + C# reactivation (~3–4 days)

**Goal:** tools working and the rust knocked off C#. Fast and ruthless; this is a refresh.

**Topics:** SDK + IDE + Docker; `dotnet` CLI (`new`, `build`, `run`, `test`, `add package`); C# refresh focused on what changed and what carries interview weight:

- **Records** (`record`, `record struct`): value equality, `with` expressions. For DTOs and value objects. Not for mutable EF entities.
- **Pattern matching** (switch expressions, property/relational/list patterns): central to clean domain logic.
- **Nullable reference types** (`<Nullable>enable</Nullable>`): compiler-enforced null intent. Keep it on. Prevents a class of `NullReferenceException`.
- **`async`/`await` internals:** `Task` vs `ValueTask`, `await` compiles to a state machine, `ConfigureAwait`, why `.Result`/`.Wait()` deadlock. The single most-asked .NET interview area. Do not skim.
- **LINQ:** `Select`/`Where`/`GroupBy`/`Aggregate`, deferred execution.
- **`IEnumerable<T>` vs `IQueryable<T>`:** in-memory vs an expression tree EF translates to SQL. Mixing them up pulls whole tables into memory. Interview favorite.
- **Collections, `Span<T>` awareness, exceptions, `IDisposable`/`using`, DI as a concept.**

**Ticketing exercise (scratch console, optional if confident):** model `Event`, `TicketType`, `Inventory`, and a `Hold` with an expiry; compute available quantity; use records for read rows and pattern matching for `EventStatus`. Then write a small concurrency warm-up: spin up several tasks that decrement the same available-count with and without a `lock`/`Interlocked` and watch the race. This previews the Phase 5 oversell problem.

**Common mistakes:** turning NRTs off because warnings annoy you; `async void` (only for event handlers); blocking on async with `.Result`.

**Interview questions seeded:** explain `async`/`await` under the hood; `IEnumerable` vs `IQueryable`; what is a record and when; why is `.Result` dangerous.

**Milestone:** clean repo, NRTs enabled, you can speak to each refreshed concept.

---

# Phase 1 — ASP.NET Core + EF Core core mechanics (Weeks 1–3)

End state: a working, intentionally un-layered ticketing API backed by Postgres, with multi-tenancy baked in.

## Week 1 — ASP.NET Core fundamentals

**Mon–Tue (~5h):** Hosting model and DI container. `WebApplication`/`builder`, the generic host, the built-in DI container (`AddScoped`/`AddTransient`/`AddSingleton`, constructor injection). Why it exists: decouples construction from use, makes testing and swapping trivial. Internals: the container resolves a dependency graph; lifetime controls instance reuse. Mistake: injecting a `Scoped` service (like `DbContext`) into a `Singleton` (the captive-dependency bug); service-locator instead of constructor injection.

**Wed–Thu (~5h):** Middleware pipeline + configuration + logging. Middleware is an ordered chain of `RequestDelegate`s; order matters (exception handling early, auth before authorization). Options pattern (`IOptions<T>`). `ILogger<T>` with structured logging (log values, not interpolated strings).

**Fri (~2.5h):** Controllers vs Minimal APIs. We use controllers (closer to enterprise norms, richer for learning); one minimal-API slice later for contrast.

**Sat–Sun (~10h):** Project bootstrap. (Done by the scaffold: structured logging, configuration, correlation-ID middleware, built-in OpenAPI, tenant-scoped controllers.)

**Topic deep-dive — the middleware pipeline (interview-heavy):** cross-cutting concerns belong in composable layers. Rough order: exception handling, HTTPS redirect, routing, CORS, authentication, authorization, endpoints. Mistakes: authorization before authentication; wrong `UseRouting`/`UseEndpoints` order. Questions: walk through the request pipeline; where does auth sit and why; `Use` vs `Map` vs `Run`.

**Milestone (W1):** API runs, OpenAPI loads, logs are structured with correlation IDs, config is typed.

## Week 2 — EF Core: modeling, migrations, persistence

**Mon–Tue (~5h):** `DbContext` (unit-of-work + identity-map), `DbSet<T>`, Fluent API in `IEntityTypeConfiguration<T>`. Model one-to-many (Event→TicketType) and one-to-one (TicketType→Inventory), value-converted money and enums. Internals: EF tracks loaded entities and diffs on `SaveChanges` (snapshot change tracking).

**Wed–Thu (~5h):** Migrations. Versioned, code-generated schema deltas; `dotnet ef migrations add`, `database update`. Schema lives in source control. Create the initial migration, run Postgres in Docker, apply, inspect the generated SQL and tables (verify EF generated what I would have by hand).

**Fri (~2.5h):** Querying + saving. `ToListAsync`, `FirstOrDefaultAsync`, `AnyAsync`; `Include` for eager loading; `Select` projections.

**Sat–Sun (~10h):** Make it real. (Done by the scaffold: tenant create/list, event browse/create/get, ticket-type create, all async, all tenant-scoped via the global query filter.)

**Topic deep-dive — change tracking and N+1 (high interview value):** change tracking exists so you persist a graph in one `SaveChanges`. Tracked entities carry state (`Added`/`Modified`/`Unchanged`/`Deleted`). N+1: lazy-loading a relation per row fires one query per row; fix with `Include` or a projection. Use `AsNoTracking` for read endpoints. Questions: how does EF know what to update; what is N+1 and how to detect/fix; tracking vs no-tracking; when does an `IQueryable` actually execute.

**Milestone (W2):** events, ticket types, and inventory persist through EF Core with migrations in source control; you can read and explain the generated SQL.

## Week 3 — Error handling, logging discipline, first end-to-end flow

**Mon–Tue (~5h):** Global error handling. `IExceptionHandler` / exception-handling middleware; return ProblemDetails (RFC 7807) for a consistent machine-readable error contract; map domain exceptions to HTTP status. Mistakes: leaking stack traces; returning 200 with an error body. (The scaffold has a minimal `GlobalExceptionHandler`; this week expands it.)

**Wed–Thu (~5h):** Logging strategy + the event state machine. Log levels and what belongs at each; never log secrets/PII. Model `Event` status transitions (Draft → OnSale → Closed) as explicit, guarded transitions, not free-form status writes (mirrors my prepayment-order Pending-state design). Reject illegal transitions with 409/422 ProblemDetails.

**Fri (~2.5h):** Pagination + filtering on the event browse endpoint.

**Sat–Sun (~10h):** Consolidate. Complete the event lifecycle endpoints with proper errors, logging, pagination, and state guards. Tag the repo `v1-naive`.

**Milestone (W3) / MONTHLY CHECKPOINT 1:** a working ticketing API: tenants, events, ticket types, inventory, lifecycle transitions, persistence, structured logs, RFC 7807 errors, pagination, multi-tenant isolation. Intentionally un-layered. **Architecture review prompt:** list everything awkward now (rules in controllers, hard-to-test handlers, `DbContext` everywhere). That pain motivates Phase 2.

**Interview-readiness check 1:** answer hosting/DI/middleware/EF-tracking/async questions cold and demonstrate them in your own code.

---

# Phase 2 — Validation, error contracts, testing, Clean Architecture (Weeks 4–5)

## Week 4 — Validation + API polish + testing foundations

**Mon–Tue (~5h):** Validation done properly. Model binding + `[ApiController]` automatic 400s; FluentValidation for non-trivial rules (event start in the future, price > 0, capacity > 0, currency valid, cross-field rules). Trivial required/length checks can stay as data annotations; do not over-engineer.

**Wed–Thu (~5h):** API versioning (`Asp.Versioning`) and action filters. Same versioning discipline I used splitting V1/V2 prepayment specs in Kong, now in code.

**Fri (~2.5h):** Set up xUnit test projects; first unit tests for the hold/availability logic and a validator.

**Sat–Sun (~10h):** Testing the right things. The testing pyramid (many fast unit tests on domain/application logic, fewer integration, very few end-to-end). Mock dependencies with NSubstitute (or Moq); do not mock what you do not own deeply. Unit-test the event state machine and the hold logic thoroughly. Introduce the `Hold` concept with single-threaded reservation logic plus tests (correctness first; concurrency is Phase 5).

**Topic deep-dive — testing strategy (you will be asked to describe yours):** domain/application logic to unit tests (no DB, no HTTP); persistence and endpoints to integration tests against a real Postgres in a container; minimal end-to-end. Mistakes: mocking `DbContext` (brittle, lies; use a real DB in a container); over-mocking until tests assert nothing; no tests on money math or state transitions. Questions: describe your testing strategy; unit vs integration line; how do you test EF Core code; what do you not mock.

**Milestone (W4):** robust validation, versioned API, a meaningful unit-test suite, green on every push.

## Week 5 — Refactor to Clean Architecture + integration tests

**Mon–Wed (~7.5h):** Introduce Clean Architecture. Four layers: **Domain** (entities, value objects, rules, no framework refs), **Application** (use cases, ports/interfaces, validators, orchestration), **Infrastructure** (EF Core, external clients, file storage; implements Application's interfaces), **Api** (controllers, DI wiring, middleware). Dependencies point inward; the dependency rule is the whole idea. When NOT to use it: trivial or CRUD-only apps. Physically split the solution; move logic out of controllers into use-case handlers; define repository/port interfaces in Application, implement in Infrastructure.

**Thu (~2.5h):** Optionally add MediatR-style or hand-rolled request handlers to make use cases first-class. Keep it light.

**Fri (~2.5h):** Update the README with a layered architecture diagram.

**Sat–Sun (~10h):** Integration tests with real infrastructure. `WebApplicationFactory<T>` spins up the API in-memory; Testcontainers starts a throwaway Postgres per run so integration tests hit a real database. Write integration tests for the full "create event → add ticket type → browse → get" flow and for tenant isolation, against containerized Postgres.

**Topic deep-dive — Clean Architecture vs alternatives (architecture interview core):** protects long-lived business logic from churny frameworks; makes the testable core obvious. Cost: more projects, mapping, ceremony; easy to cargo-cult. Mistakes: EF entities as the domain model with no separation; referencing Infrastructure from Domain; five layers for a CRUD app. Questions: explain the dependency rule; where do EF entities live; when would you not use it; how does it help testing.

**Milestone (W5) / ARCHITECTURE REVIEW CHECKPOINT:** the same features on a layered, testable architecture with unit + integration tests. You can articulate, from experience, what layering bought versus `v1-naive`. **Code review checkpoint:** re-read your own PRs; check naming, handler size, no business rule in a controller.

---

# Phase 3 — Authentication & Authorization (Weeks 6–7)

I know authN vs authZ conceptually; this is the .NET stack and getting authorization right, where most candidates are weak. This is also where multi-tenancy stops trusting an `X-Tenant-Id` header and starts trusting a tenant claim on the authenticated principal.

## Week 6 — Authentication with JWT

**Mon–Tue (~5h):** ASP.NET Core Identity for user storage + password hashing (PBKDF2), or a minimal custom user store. Never plaintext or home-rolled hashes. Register/login endpoints.

**Wed–Thu (~5h):** JWT bearer tokens. A JWT is signed, not encrypted: header.payload.signature. The API validates issuer, audience, lifetime, signature on every request. Claims live in the payload; signature proves integrity, not secrecy, so never put secrets in a JWT. Issue a JWT on login carrying user id, role, and the tenant id for organizer staff.

**Fri (~2.5h):** Refresh tokens. Short-lived access token + longer-lived, revocable, server-stored refresh token. Mistake: long-lived access tokens with no revocation; storing refresh tokens unhashed.

**Sat–Sun (~10h):** Wire it through. Three principals: Customer (platform-global), OrganizerStaff (tenant-scoped via tenant claim), PlatformAdmin. Login issues JWTs; protect endpoints with `[Authorize]`; refresh flow working. Replace `TenantResolutionMiddleware`'s header read with reading the tenant claim from the principal, so clients can no longer choose their own tenant.

**Topic deep-dive — JWT (asked in nearly every API interview):** stateless tokens enable horizontal scaling without shared session state (relevant to the Kubernetes goal). Mistakes: trusting an unvalidated token; ignoring expiry/clock skew; "signed means encrypted" confusion; no revocation strategy. Questions: how does validation work; signed vs encrypted; access vs refresh and why both; how do you revoke a JWT (honest answer: you cannot truly revoke a stateless JWT without server-side state).

## Week 7 — Authorization: roles, claims, policies, resource-based

**Mon–Tue (~5h):** Roles vs claims/policies. Role-based (`[Authorize(Roles="OrganizerStaff")]`) is coarse; claims/policy-based (`AddPolicy` with requirements evaluated by `AuthorizationHandler`s) is scalable. Policies like `CanManageEvents` (organizer staff for their tenant), `CanAdministerPlatform` (platform admin).

**Wed–Thu (~5h):** Resource-based authorization (the part that matters most here). "A customer may read an order only if it is theirs" and "staff may touch an event only within their tenant" depend on the resource instance, not just a role. Use `IAuthorizationService.AuthorizeAsync(user, resource, requirement)` with handlers that compare ownership and tenant.

**Fri (~2.5h):** Security hygiene. Secrets via user-secrets locally and environment/secret stores in containers, never in committed `appsettings.json`; HTTPS; rate limiting (formalized in Phase 6); OWASP API Top 10 at a high level. Carry my `RequestId` idempotency habit into write endpoints (booking especially).

**Sat–Sun (~10h):** Consolidate + test authz. Integration tests proving a customer is 403'd on another customer's order, staff are blocked from another tenant's event (404/403), and platform admin is allowed.

**Topic deep-dive — authorization models (separates senior from junior):** claims/policy over roles because roles explode and hard-code rules; policies centralize and compose them. Mistakes: scattered `if` checks instead of policies/handlers; forgetting resource-level ownership (broken object-level authorization is a top real breach class). Questions: roles vs claims vs policies; how to enforce "only the owner can see this"; where should authorization live.

**Milestone (W7) / MONTHLY CHECKPOINT 2:** full authentication and a real authorization model (roles + policies + resource-based + tenant-scoped), tested, with the tenant now coming from the token. **Interview-readiness check 2:** whiteboard the auth flow and defend the authz design, including the honest JWT-revocation answer.

---

# Phase 4 — External integrations, resilience, Redis caching, CQRS read models (Weeks 8–9)

## Week 8 — Calling external APIs with resilience

**Mon–Tue (~5h):** `IHttpClientFactory` + typed clients. Why: `new HttpClient()` per call exhausts sockets; a single static one ignores DNS changes; the factory pools and recycles handlers. Build a typed client for a mock Payment Provider.

**Wed–Thu (~5h):** Resilience with `Microsoft.Extensions.Http.Resilience` / Polly: retry with jittered backoff, timeout, circuit breaker, fallback. When NOT to retry: non-idempotent operations or 4xx; retrying a charge can double-charge, so make external writes idempotent or do-not-retry. Add a resilience pipeline to the payment client and make the charge idempotent via a request key.

**Fri (~2.5h):** Build a stub external service (or WireMock) to simulate latency, failures, and 500s and watch the circuit breaker trip.

**Sat–Sun (~10h):** Integrate into the booking flow. Checkout calls the payment provider (resilient, idempotent). Integration-test the failure paths (provider down means graceful behavior, not a 500 to the buyer, and no ticket issued).

**Topic deep-dive — resilience patterns (strong system-design signal):** circuit breakers stop hammering a failing dependency and shed load. Mistakes: retry storms amplifying an outage; retrying non-idempotent writes; zero/infinite backoff. Questions: why `IHttpClientFactory`; retry vs circuit breaker; how to avoid double-charging on a retry.

## Week 9 — Redis caching + CQRS read models

**Mon–Tue (~5h):** Redis + cache-aside. Run Redis in Docker; `StackExchange.Redis` or `IDistributedCache`. Cache-aside: check cache, on miss load from DB and populate with a TTL. What to cache here: event details and availability counts (read-heavy, hot during an on-sale). What NOT to cache: anything where staleness causes a wrong purchase decision without a reconciling check at write time.

**Wed–Thu (~5h):** Invalidation and hazards. The hard part is invalidation, not caching. Invalidate/refresh on writes. Cache stampede (many misses hitting the DB when a hot key expires) and mitigations (locking, jittered TTLs). `IMemoryCache` vs Redis and why distributed matters once there are multiple pods.

**Wed–Thu also — CQRS read models:** separate the transactional write side (holds, orders) from the read side (browse, search, availability). Build denormalized read models (for example `EventListItem`, `EventAvailabilityView`) updated on writes or domain events, and serve browse/search from them. Teaches the pattern and its eventual-consistency tradeoff.

**Fri (~2.5h):** Measure. Timing logs/metrics showing the cache reduced DB load.

**Sat–Sun (~10h):** Consolidate + tests. Finalize caching and read models; test cache hit/miss, invalidation, and read-model consistency.

**Topic deep-dive — caching (interview staple, easy to get wrong):** cut latency and load on slow/expensive reads. Mistakes: caching volatile data; no invalidation; no TTL; per-user data under shared keys (data leak); ignoring stampede on hot keys. Questions: cache-aside vs write-through; how to invalidate; `IMemoryCache` vs Redis; what is a stampede and how to prevent it; what should you never cache.

**Milestone (W9):** resilient external payment integration, the right things cached in Redis with correct invalidation, and CQRS read models serving browse/search, all containerized and tested.

---

# Phase 5 — Concurrency, messaging, background services, the booking saga (Weeks 10–11)

This is the centerpiece. It includes the single highest-value thing the project teaches: preventing oversell under contention.

## Week 10 — Concurrency + RabbitMQ + the booking saga

**Mon–Tue (~5h) — OVERSELL PREVENTION (the marquee):** the problem: many buyers try to take the last tickets at once and the system must never sell more than exist. Implement and compare three strategies, and write up the tradeoffs:
- Optimistic concurrency: the `xmin` concurrency token already on `Inventory` (plus retry on conflict).
- Pessimistic locking: serializable transaction or `SELECT ... FOR UPDATE`.
- Redis atomic decrement / distributed lock for the hot path.
This is a phase highlight and one of the most interviewed senior topics.

**Wed–Thu (~5h) — RabbitMQ + outbox:** exchanges, queues, bindings, routing keys, acks; at-least-once delivery and therefore idempotent consumers. Start with `RabbitMQ.Client` to see the primitives, then MassTransit for the production abstraction. The dual-write problem: you cannot atomically write to the DB and publish to a broker. Outbox pattern: write the event to an outbox table in the same transaction as the state change; a background dispatcher publishes it. Publish `TicketsSold`, `OrderConfirmed`, `PaymentFailed`; a consumer reacts (notification record).

**Fri (~2.5h):** Idempotent consumers + poison messages: dedupe by message id; dead-letter after N retries.

**Sat–Sun (~10h) — the booking saga end to end:** hold (TTL) then pay then confirm then issue tickets; on payment failure or hold expiry, release inventory (compensation). Outbox guarantees the events. Test the happy and failure paths.

**Topic deep-dive — messaging + outbox (high-signal in system-design rounds):** async/events decouple producers from consumers, smooth spikes, enable independent scaling and retry. Mistakes: assuming exactly-once (it is at-least-once; design idempotent consumers); the dual-write trap; no dead-letter; treating the broker as a database. Questions: how do you guarantee an event publishes with the DB change (outbox); at-least-once vs exactly-once; how do you handle a poison message; when events vs synchronous calls.

## Week 11 — Background services, scheduled work, file storage

**Mon–Tue (~5h):** `BackgroundService`/`IHostedService` for long-running in-process work; `System.Threading.Channels` for in-process producer/consumer. Respect cancellation tokens for graceful shutdown. Build the hold-expiry release as a background service (release inventory for holds past their TTL).

**Wed–Thu (~5h):** Scheduled domain jobs. In-process timer scheduling vs Hangfire/Quartz for durable, observable, distributed scheduling. Reach for Hangfire when jobs must survive restarts, be visible, and not double-run across pods. Mistake: a naive timer in every replica running the job N times (the .NET analog of the OutSystems WakeTimer vs BPT problem; scope the schedule to one instance or use a durable scheduler). Build settlement reconciliation and any recurring cleanup, safe under multiple replicas.

**Fri (~2.5h):** File storage. Files in object storage (local folder then MinIO/S3); metadata in the DB; never large blobs in the relational DB. Stream uploads/downloads; validate type/size; authorize by ownership. Generate the ticket PDF on confirmation and store it; download is ownership-checked.

**Sat–Sun (~10h):** Consolidate. Hold-expiry and reconciliation running reliably; ticket PDF storage working; tests where feasible. Tag `v2-eventdriven`.

**Milestone (W11) / MONTHLY CHECKPOINT 3:** resilient sync integration, Redis caching, CQRS read models, safe concurrent inventory with three compared strategies, event-driven async with an outbox, durable scheduled jobs, and file storage. Recognizably something a real company would build. **Architecture review checkpoint:** sketch the full component diagram (API, DB, Redis, RabbitMQ, object storage, background workers) and walk "browse then hold then pay then confirm then issue ticket". **Interview-readiness check 3:** defend the concurrency-strategy choice, the outbox, idempotency, and scheduling-under-replicas.

## Phase 5b — Real-time + surge (woven in; core SignalR, stretch waiting room)

**SignalR live availability (core):** push live availability counts to clients over SignalR; understand the multi-replica backplane problem (in-process hub state breaks once you scale; a Redis backplane fixes it).

**Virtual waiting room / queue-based load leveling (stretch, the standout senior feature):** when an on-sale opens, admit users in controlled batches via a Redis sorted set plus RabbitMQ, and push real-time queue position. Demonstrates backpressure and load shedding. If time is short, convert this to a written system-design section instead of a full build.

---

# Phase 6 — Containerization, CI/CD, Kubernetes, observability (Weeks 12–13)

## Week 12 — Docker + CI/CD

**Mon–Tue (~5h):** Multi-stage Dockerfile (SDK image builds, runtime image runs) for small, secure images; `.dockerignore`; run as non-root; .NET 10 default images are Ubuntu-based (source: https://developers.redhat.com/articles/2025/11/17/net-10-now-available-rhel-and-openshift). Mistake: shipping the SDK image to production; copying secrets into layers.

**Wed–Thu (~5h):** Compose the whole stack. `docker-compose` (or Aspire if you want) bringing up API + Postgres + Redis + RabbitMQ + MinIO with one command. The local prod-like environment and a README centerpiece.

**Fri (~2.5h):** Health checks (`/health/live`, `/health/ready`) verifying DB/Redis/broker reachability; these become Kubernetes probes. Environment-based configuration.

**Sat–Sun (~10h):** GitHub Actions CI: restore, build, run unit + integration tests (Testcontainers works in CI), build/push the image to GHCR. Then retrofit branch protection on `main` to require the CI check (closing the loop from the Git workflow). Green CI on every PR.

**Topic deep-dive — Docker + CI/CD (table stakes):** multi-stage for a tiny runtime image with no build tooling in prod. Mistakes: SDK images in prod; secrets baked into images; no `.dockerignore`; CI that skips integration tests. Questions: why multi-stage; how to keep secrets out of images; what does your pipeline run on a PR.

## Week 13 — Kubernetes fundamentals + observability

**Mon–Tue (~5h):** K8s core objects: Pod, Deployment (declarative replicas + rollout), Service (stable networking), ConfigMap/Secret, Ingress, liveness/readiness probes (wired to the Phase 6 health endpoints), resource requests/limits, a word on HPA. Readiness gates traffic until dependencies are up; liveness restarts a wedged pod. Write the manifests.

**Wed–Thu (~5h):** Deploy locally with kind or minikube; expose via Ingress; **scale to multiple replicas and watch a rolling update**. This is where the distributed-lock (oversell) and SignalR-backplane lessons click, because in-process locks and in-process hub state break across replicas. Enough K8s for mid-level; aim for fluent fundamentals, not CKA.

**Fri (~2.5h):** Built-in rate limiting; graceful shutdown via cancellation tokens so in-flight requests and background jobs finish on pod termination.

**Sat–Sun (~10h):** Observability, the three pillars. Structured logs (Serilog, sinks); metrics + distributed tracing via OpenTelemetry; correlation/trace IDs across the request and into RabbitMQ consumers and SignalR. Optionally view traces locally (Jaeger or the Aspire dashboard).

**Topic deep-dive — K8s + observability (mid-level expectations):** declarative, self-healing, scalable container deployment. Mistakes: no resource limits (OOM/noisy neighbor); conflating liveness and readiness (restart loops); unstructured logs you cannot query; no tracing across async hops. Questions: Pod vs Deployment vs Service; liveness vs readiness; how to debug a pod that keeps restarting; how to trace a request across services and a queue.

**Milestone (W13) / MONTHLY CHECKPOINT 4:** containerized, CI/CD'd, deployed to local Kubernetes with health probes, rate limiting, graceful shutdown, structured logs, metrics, and tracing. A production-shaped system. **Code review checkpoint:** review the Dockerfile, manifests, and pipeline as if a teammate wrote them.

---

# Phase 7 — System design, architecture set-pieces, interview prep, polish (Week 14)

**Mon (~2.5h):** Vertical Slice Architecture by doing. Implement one feature (for example "export an event sales report" or "issue a ticket PDF") as a self-contained slice (request, handler, response in one folder, minimal cross-layer indirection). Then write the tradeoff in the README: Clean Architecture optimizes for a shared, protected domain and large teams; Vertical Slice optimizes for feature locality and speed at the cost of some duplication and weaker central enforcement. Many teams blend them.

**Tue (~2.5h):** Monolith vs Microservices, written against this project. The strong answer: this is a modular monolith and should stay one for a team of its likely size. Microservices buy independent deploy/scale and team autonomy at the cost of distributed-systems complexity. Split only when a module has a different scaling profile or a separate team owns it. Note where the clean seams are (ticketing/inventory vs payments vs notifications) and that the outbox + broker already let you extract a service later without a rewrite. Restraint reads as senior.

**Wed–Thu (~5h):** System design fundamentals + mock problems. Vocabulary: load balancing, horizontal vs vertical scaling, statelessness, caching layers, read replicas and sharding basics, CAP/consistency tradeoffs, queues for decoupling, idempotency, the outbox, rate limiting, and for this domain: high-contention inventory, queue-based load leveling, multi-tenant isolation strategies (shared schema with discriminator vs schema-per-tenant vs DB-per-tenant), CQRS and eventual consistency. Practice out loud or on paper in ~30–40 minutes each: (a) design Ticketmaster (use your own system), (b) a payments/ledger service, (c) a notification service.

**Fri (~2.5h):** Final polish. README: architecture diagram, run instructions, feature list, a design-decisions section (every choice with its tradeoff). Clean commit history. Tag `v3-production`.

**Sat–Sun (~10h):** Mock interviews + gaps. A behavioral set, a .NET fundamentals set, and one live system-design problem. Patch weak answers in the codebase or notes.

**Milestone (W14) / FINAL CHECKPOINT:** a deployable, observable, event-driven, secured, multi-tenant ticketing platform you designed, built, and can defend end to end, plus rehearsed answers across language, framework, architecture, and system design.

---

# Checkpoint cadence (so nothing slips)

- **Weekly milestone:** the bolded line ending each week. If you cannot demo it, do not advance.
- **Monthly checkpoints:** end of W3, W7, W11, W13. Each is "demo the whole system + review the architecture".
- **Code review checkpoints:** W5, W13 (and re-read your own PR before every merge).
- **Architecture review checkpoints:** W3 (name the pain), W5 (did layering help), W11 (full component diagram).
- **Interview-readiness checks:** W3, W7, W11, plus the W14 mocks.

---

# Architecture set-pieces (reference summaries)

- **Clean Architecture:** dependencies point inward (Domain ← Application ← Infrastructure/Api). For long-lived domains and larger teams. Avoid for trivial CRUD. The dependency rule is the exam question.
- **Vertical Slice:** organize by feature, minimize per-feature indirection. For feature velocity and small/medium teams. Tradeoff: some duplication, weaker central enforcement. Often blended with Clean boundaries.
- **Monolith vs Microservices:** default to a modular monolith; earn the right to split. Microservices solve org-scaling and independent-deploy problems, not messy-code problems. Event-driven seams (outbox + broker) let you extract a service later. "I would not split this yet, and here is when I would" is a senior signal.
- **Testing strategy:** many unit tests on domain/application logic; integration tests against real Postgres in Testcontainers via `WebApplicationFactory`; minimal end-to-end. Do not mock `DbContext`. Always test money math and state transitions, and for this project, the concurrency/oversell logic.
- **System design fundamentals:** statelessness for horizontal scale, caching layers (and what not to cache), queues for decoupling and load-smoothing, idempotency, the outbox, read replicas/sharding basics, consistency tradeoffs, rate limiting, health/probes, plus the domain-specific trio: contention control, load leveling, multi-tenancy.

---

# Final deliverables and interview prep

**Portfolio:** this ticketing platform as one strong, complete, production-shaped system. Do not ship half-finished side projects; a reviewer trusts one finished system far more than several abandoned ones. Optional small secondary only if time: a focused idempotent payment-webhook receiver or a rate-limited cached public API.

**Resume framing:** reframe OutSystems experience in transferable terms ("designed and delivered REST APIs, async processing pipelines, message-driven integrations, normalized schemas for banking; led a team"); do not undersell it as low-code. Add a .NET project line for the ticketing platform with concrete tech and outcomes (ASP.NET Core 10 / EF Core / PostgreSQL, multi-tenant, JWT + policy + resource-based authz, Redis caching, RabbitMQ with a transactional outbox, concurrency-safe inventory, SignalR, Dockerized, Kubernetes, CI/CD, OpenTelemetry). One to two pages; link the repo and a design-decisions README. Apply as mid-level; let the architecture and leadership depth show that you punch above it.

**Interview topic checklist:**
- Language/runtime: `async`/`await` internals, `Task` vs `ValueTask`, `IEnumerable` vs `IQueryable`, records/structs, value vs reference types, GC basics, `IDisposable`.
- ASP.NET Core: middleware pipeline, DI lifetimes and the captive-dependency bug, filters, model binding, minimal vs controllers.
- EF Core: change tracking, N+1, tracking vs no-tracking, migrations, transactions, when to drop to raw SQL.
- Auth: JWT validation, signed vs encrypted, access/refresh, roles vs claims vs policies, resource-based authz, the honest "you cannot truly revoke a stateless JWT" answer.
- Concurrency: optimistic vs pessimistic vs distributed locking, when each, why in-process locks fail across replicas, how you prevent oversell.
- Resilience/caching/messaging: `IHttpClientFactory`, retry vs circuit breaker, idempotency, cache-aside + invalidation + stampede, at-least-once + outbox + dead-letter.
- Real-time + scale: SignalR backplane, queue-based load leveling, multi-tenancy isolation strategies, CQRS and eventual consistency.
- Ops: multi-stage Docker, K8s probes (liveness vs readiness), debugging a restart loop, tracing across async boundaries.
- Architecture: Clean vs Vertical Slice, monolith vs microservices with restraint, and the rehearsed design problems.
- Behavioral: lead with leadership and delivery stories; a clear "why .NET" narrative (depth and full-stack ownership over a tooling abstraction).

**Signs of readiness:** stand up a new ASP.NET Core + EF Core service from empty folder to first endpoint without a tutorial; explain the pipeline, DI lifetimes, and EF change tracking from your own code; defend the auth model and its limits; explain the failure modes of Redis, RabbitMQ + outbox, background jobs, and resilient external calls; explain and defend your oversell-prevention strategy; containerize, build a CI/CD pipeline that runs your tests, and deploy to Kubernetes with working probes; argue against over-engineering; take a design prompt to API + data model + scaling + failure modes + tradeoffs in ~30 minutes; show one finished, documented, deployable system.

---

# ENVIRONMENT AND HOW TO RUN (current, verified — read before running anything)

This is the real setup, including things learned the hard way. It is not the same as the idealized Phase 0 text above.

## Toolchain
- **.NET 10 SDK (10.0.300)** required. Pinned by `global.json` at the repo root (`rollForward: latestFeature`). Multiple SDKs (9.x and 10.x) are installed; the pin forces 10.0.300.
- **IDE: Visual Studio 2026 (18.x)** for build / F5 / debugging / the `.http` runner / Test Explorer. **Visual Studio 2022 CANNOT build this project.** The .NET 10 SDK requires MSBuild 18; VS 2022 is permanently on MSBuild 17, so it fails with `NETSDK1045` ("requires at least version 18.0.0 of MSBuild"). The `dotnet` CLI builds fine regardless, because it uses the SDK's own bundled MSBuild 18 independent of any installed VS.
- **`dotnet-ef` global tool must be v10**: `dotnet tool update --global dotnet-ef --version 10.0.0` (a v9 tool mismatches the EF Core 10 packages).

## Database
- PostgreSQL 17 in Docker via `docker compose up -d`.
- **Host port is 5433, not 5432.** A native PostgreSQL service already owns 5432 on the dev machine and silently intercepts connections (causes `28P01 password authentication failed`). The compose mapping is `5433:5432` and `appsettings.json` uses `Port=5433`. The container-internal port is still 5432.
- Local-dev credentials: user/password/db all `ticketing`.
- **Migrations are applied manually** with `dotnet ef database update` (Program.cs does NOT auto-migrate at startup).

## Run
```bash
docker compose up -d
dotnet build TicketingPlatform.sln -c Release                      # or build/F5 in VS 2026
# EF is cross-project since the Clean Architecture refactor: migrations live in Infrastructure,
# the startup project is Api. Both flags are required for every dotnet ef command:
dotnet ef database update --project src/TicketingPlatform.Infrastructure --startup-project src/TicketingPlatform.Api
# new migration:
dotnet ef migrations add <Name> --project src/TicketingPlatform.Infrastructure --startup-project src/TicketingPlatform.Api
dotnet run --project src/TicketingPlatform.Api                     # http://localhost:5000, routes under /api/v1
dotnet test                                                        # 41 unit tests (domain + validators)
```
- API listens on `http://localhost:5000` (launchSettings). `GET /` returns 404 by design (Web API, no home page). OpenAPI spec at `/openapi/v1.json` in Development; there is no Swagger UI. Verify endpoints via `requests.http`.
- **One instance on port 5000 at a time.** A second `dotnet run` / F5 fails with an address-in-use error; and building while the app runs fails with `MSB3027` because Windows locks the output `.exe`. Stop the app before you build.

## Hard-won gotchas
- "Build succeeded" is not "works": the state-machine bug compiled cleanly and would have shipped. Lean on tests.
- Remote: GitHub `NikolozPapaskiri/ticketing-platform`. Conventional Commits; milestone tags (`v1-naive` is pushed).

---

# STATUS (update this as we go)

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
  - **Clean Architecture refactor, stages 1–4 of 5 done:** solution split into `Domain` (entities + state machine, zero deps) ← `Application` (contracts, validators, `ITenantContext` port, `Abstractions/`) ← `Infrastructure` (`Persistence/TicketingDbContext` + migrations + `AddInfrastructure(connString)` DI extension; Npgsql + EF Design packages live here) ← `Api` (controllers, middleware, filter, composition root). EF verified cross-project: `migrations list` works, `has-pending-model-changes` = none, runtime smoke test green.
  - Security: `System.Security.Cryptography.Xml` pinned to 10.0.9 (NU1903 via EF Design cleared).
- **Now — finish Phase 2:**
  1. **(current) Clean Architecture stage 5:** Application services + repository ports; thin controllers; controllers stop referencing `TicketingDbContext`. 5a = tenant slice (`Result`/`Result<T>` in `Application/Common`, `ITenantRepository` port, `TenantRepository` in Infrastructure, `TenantService`, thin `TenantsController`, DI registration). 5b = event slice (same pattern: `IEventRepository`, `EventService` owning pagination/create/add-ticket-type/publish/close, thin `EventsController` mapping `Result` errors to 404/409).
  2. Integration tests: `WebApplicationFactory` + Testcontainers against real Postgres — tenant isolation, transition endpoints (204/409/404), pagination + filtering, duplicate slug 409, full create → browse → get flow.
  3. Introduce the `Hold` concept (TTL reservation, single-threaded correctness + unit tests; concurrency is Phase 5). Then tag `v2-clean` (architecture milestone) and update this STATUS.
- **Then (the road to the production-ready product):** Phase 3 auth **built inside this repo** (Identity user store + JWT + refresh tokens; roles/policies/resource-based authz; tenant claim replaces the `X-Tenant-Id` header) → Phase 4 (payment client with resilience, Redis cache-aside, CQRS read models) → Phase 5 (oversell prevention 3 ways, RabbitMQ + outbox, booking saga, background services, ticket PDFs; tag `v2-eventdriven`) → Phase 5b (SignalR) → Phase 6 (Dockerfile + compose full stack, GitHub Actions CI, Kubernetes, health checks, rate limiting, OpenTelemetry) → Phase 7 (vertical-slice set piece, design write-ups, README polish; tag `v3-production`).
- **Decision (recorded 2026-07):** the platform stays **self-contained** — no external auth server or third-party project integration; everything is built in this repository per the original plan.

When you finish a phase, move its items into "Done" and update the "Now" list.
