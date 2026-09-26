# CLAUDE.md — project context and learning plan

This file is the durable context for this repository. Claude Code reads it at the start of every session. It defines who I am, how you (Claude Code) should work with me, how to run the system, and where we are right now. The original phase-by-phase build plan is in `docs/BUILD_PLAN.md`.

---

## Who I am and what I am doing

I am a Lead OutSystems developer (5+ years, banking domain: after-sales loan servicing, async prepayment flows, REST APIs, normalized schemas, Kong gateway, C# extensions with MimeKit). I already hold senior engineering judgment. I am reactivating and deepening .NET to interview and work as a mid-to-senior .NET backend engineer, and to be able to rewrite production systems in .NET.

This repo is my flagship learning project: a **multi-tenant event ticketing and booking platform** (a working "design Ticketmaster"). It is one system that I grow across all phases. The project was chosen to teach the senior topics my banking work did not exercise (hard concurrency under contention, surge load leveling, real-time push, multi-tenancy, CQRS) while reusing patterns I already know (sagas, idempotency, messaging, state machines).

**Target framework:** .NET 10 (LTS), C# 14, ASP.NET Core 10, EF Core 10. .NET 10 shipped November 11 2025 and is supported until November 2028; .NET 8 (prior LTS) is supported only until November 2026, so we build on .NET 10. Sources: https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/ and https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core. Day-to-day reference: https://learn.microsoft.com/en-us/dotnet/.

---

## How you (Claude Code) should work with me

**Working mode.** `docs/HANDS_ON_LEARNING_PLAN.md` §1 is the working agreement for learning work: I implement, you coach and review. I may hand you ops or CI unblockers to do yourself. When a task could be a learning exercise and I have not said which mode, ask once.

1. **Calibrate to what I have demonstrated.** I bring senior judgment from banking work (REST/HTTP semantics, idempotency, relational modeling, async and messaging concepts, caching as a concept, code review, leading delivery), but .NET mechanics start from the basics and move faster only after I demonstrate them. Spend the time on .NET-specific mechanics, idioms, and the things interviewers probe.

2. **Teach, do not just autocomplete.** For every new topic, give me four things: why it exists, the internals (what actually happens under the hood), the common mistakes, and the interview questions it seeds. When you write code, explain the non-obvious .NET-specific choices, not the syntax.

3. **Verify by building.** When you write code (a task I handed you, or a reference snippet), run `dotnet build`, `dotnet ef`, and the relevant tests, and fix failures before handing it back. Never give me code you have not compiled and run. In coach mode, check my work the same way and point at the failure instead of fixing it.

4. **Enforce the gates.** Do not advance to the next learning-plan stage until I can meet the current stage's gate and answer its questions out loud. If I try to skip ahead, push back and tell me what is unfinished. Slow is fine; skipping foundations is not.

5. **Make me do the reps.** For interview-critical areas (async internals, `IEnumerable` vs `IQueryable`, EF change tracking and N+1, the authorization model, the concurrency strategies), have me explain the concept back or implement the core myself before you fill in around it. Do not let me passively read.

6. **Always surface the tradeoff and the "when NOT to."** For every pattern, tell me where it is the wrong choice. Arguing against over-engineering (when not to cache, when not to split into services, when Clean Architecture is overkill) is a senior signal I need to be able to give.

7. **Keep Git discipline.** Short-lived `feature/*` branches, Conventional Commits (`feat:`, `fix:`, `refactor:`, `test:`), small PRs, self-review the diff before merge, tag the version milestones (`v1-naive`, `v2-eventdriven`, `v3-production`).

8. **Track progress where it lives.** When a milestone completes, record it in `docs/ROADMAP.md` (product) or the learning plan (learning), and keep the Status section at the bottom of this file to the few current lines a new session needs.

9. **My working preferences.** Be direct and first-principles. Cite sources for factual claims. No em dashes, no filler, no clichés. Usefulness over politeness. If you make a mistake, say so plainly and fix it.

**What I will give you each session:** the learning-plan session or roadmap item I am on, any build or test output, and the specific thing I am stuck on or want to learn next. If it is not obvious, ask what I want to tackle.

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

## The original build plan

The phase-by-phase plan this repository was built from (Phases 0-7 with topics, internals, common mistakes, seeded interview questions and milestones, plus the checkpoint cadence, architecture set-pieces, and the interview topic checklist) is in `docs/BUILD_PLAN.md`. Every phase is complete. Use it as the reference for interview reps; the current learning path is `docs/HANDS_ON_LEARNING_PLAN.md`.

---

# ENVIRONMENT AND HOW TO RUN (current, verified — read before running anything)

This is the real setup, including things learned the hard way. It is not the same as the idealized Phase 0 text in `docs/BUILD_PLAN.md`.

## Toolchain
- **.NET 10 SDK (10.0.300)** required. Pinned by `global.json` at the repo root (`rollForward: latestFeature`). Multiple SDKs (9.x and 10.x) are installed; the pin forces 10.0.300.
- **IDE: Visual Studio 2026 (18.x)** for build / F5 / debugging / the `.http` runner / Test Explorer. **Visual Studio 2022 CANNOT build this project.** The .NET 10 SDK requires MSBuild 18; VS 2022 is permanently on MSBuild 17, so it fails with `NETSDK1045` ("requires at least version 18.0.0 of MSBuild"). The `dotnet` CLI builds fine regardless, because it uses the SDK's own bundled MSBuild 18 independent of any installed VS.
- **`dotnet-ef` global tool must be v10**: `dotnet tool update --global dotnet-ef --version 10.0.0` (a v9 tool mismatches the EF Core 10 packages).

## Database
- PostgreSQL 17 in Docker via `docker compose up -d`.
- **Host port is 5433, not 5432.** A native PostgreSQL service already owns 5432 on the dev machine and silently intercepts connections (causes `28P01 password authentication failed`). The compose mapping is `5433:5432` and `appsettings.json` uses `Port=5433`. The container-internal port is still 5432.
- Local-dev credentials: user/password/db all `ticketing`.
- In Development, API startup auto-applies migrations and seeds `admin@platform.local` / `Admin123$`.
  For migration authoring, use the cross-project `dotnet ef` commands below.

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
dotnet test                                                        # unit + integration; integration tests need Docker
```
- API listens on `http://localhost:5000` (launchSettings). `GET /` returns 404 by design (Web API, no home page). OpenAPI spec at `/openapi/v1.json` in Development; there is no Swagger UI. Verify endpoints via `requests.http`.
- Web UI listens on `http://localhost:3000` from `apps/web`. Use the UI for anonymous/customer/organizer/admin testing; do not expect the API root to render a page.
- **One instance on port 5000 at a time.** A second `dotnet run` / F5 fails with an address-in-use error; and building while the app runs fails with `MSB3027` because Windows locks the output `.exe`. Stop the app before you build.
- If the API is running and Windows locks build outputs, run tests with a separate output path, for example `dotnet test tests\TicketingPlatform.UnitTests\TicketingPlatform.UnitTests.csproj --no-restore -p:OutputPath=C:\Users\PC\Desktop\ticketing-platform\.artifacts\unit-test\`.

## Frontend runbook
```bash
cd apps/web
npm.cmd install
npm.cmd run dev
# UI: http://localhost:3000
# API expected at http://localhost:5000 unless API_BASE_URL / NEXT_PUBLIC_API_BASE_URL override it
# E2E: npm.cmd run e2e
# Existing dev server: $env:PLAYWRIGHT_SKIP_WEB_SERVER='1'; npm.cmd run e2e
```
- Use `http://localhost:3000`, not `http://127.0.0.1:3000`, for Next dev and Playwright. The 127.0.0.1 origin can break Next dev assets/HMR and make pages look like they are cycling.
- Local HTTP needs `COOKIE_SECURE=false`; real HTTPS production should keep secure cookies enabled.
- Role entry points: anonymous `/`, `/t/{slug}`, `/t/{slug}/events/{eventId}`; customer `/account`; organizer `/organizer`; platform admin `/admin`.
- Dev platform admin: `admin@platform.local` / `Admin123$`; create organizer staff from `/admin`.

## Hard-won gotchas
- "Build succeeded" is not "works": the state-machine bug compiled cleanly and would have shipped. Lean on tests.
- Remote: GitHub `NikolozPapaskiri/ticketing-platform`. Conventional Commits; milestone tags `v1-naive` through `v3-production`.
- RabbitMQ credentials are `ticketing/ticketing` everywhere. The built-in `guest` user is loopback-only, and Docker port-proxied connections do not count as loopback.
- `Microsoft.EntityFrameworkCore.Design` must stay on the Api (startup) project for the EF tools. It is design-time only (`PrivateAssets`), so it does not break the dependency rule.

---

# STATUS

Current state only. Product progress lives in `docs/ROADMAP.md`, learning progress in `docs/HANDS_ON_LEARNING_PLAN.md`, and the phase-by-phase build record in `docs/BUILD_HISTORY.md`.

- **Build plan complete.** Phases 0-7 are done and tagged `v1-naive` → `v2-clean` → `v2-eventdriven` → `v3-production`. Post-v3 work is also done: product hardening, the `apps/web` frontend, the virtual waiting room, the production-safety plan (PR 1-6) plus the distributed login limiter, observability P1-P5, and the flash-sale load test (`docs/LOAD_TEST.md`).
- **In flight (as of 2026-09-26):** `docs/ROADMAP.md` is the single source for what is next. Gate 0 (G1-G4) has shipped. Phase A slices 1-3 are merged: the model is now `Event → Performance → TicketType`, and `Event.StartsAt` is gone.
- **Decision (2026-07):** the platform stays self-contained, with no external auth server and no third-party project integration.
- **Tenancy model:** two planes. An organizer is one tenant and fails closed on a tenant mismatch; a customer has no tenant and fails closed on ownership. That is why some read paths legitimately use `IgnoreQueryFilters`. See `docs/MULTI_TENANCY.md`.
- `AGENTS.md` is the Codex-facing mirror of this file, maintained by me.
