# Ticketing Platform — Phase 1 scaffold

A multi-tenant event ticketing API. This is the **Phase 1 (naive)** slice from the project spec: ASP.NET Core 10 + EF Core 10 + PostgreSQL, with multi-tenancy baked in from the start. It is deliberately un-layered (controllers call the `DbContext` directly). You refactor it to Clean Architecture in Phase 2, after you have felt where the naive structure hurts.

## What you learn from this code

- The ASP.NET Core hosting model, the built-in DI container, and service lifetimes.
- The middleware pipeline (a correlation-id middleware and a tenant-resolution middleware) and why order matters.
- EF Core mechanics: `DbContext`, entity configuration with the Fluent API, relationships, migrations, async queries, and reading the generated SQL.
- **Multi-tenancy via a global query filter:** every tenant-owned read is scoped to the current tenant automatically.
- Structured logging with `ILogger` and log scopes.
- Optimistic-concurrency setup (Postgres `xmin`) on inventory, ready for the contention work in Phase 5.
- A minimal `IExceptionHandler` returning RFC 7807 ProblemDetails.

## Prerequisites

- .NET 10 SDK (`dotnet --version` should print 10.x). Download: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- Docker (for PostgreSQL).
- EF Core CLI tool: `dotnet tool install --global dotnet-ef`

## Run it

```bash
# 1. Start PostgreSQL
docker compose up -d

# 2. Restore packages
#    If a package version fails to resolve, see "Package versions" below.
dotnet restore

# 3. Create the database schema from the model
dotnet ef migrations add InitialCreate --project src/TicketingPlatform.Api
dotnet ef database update --project src/TicketingPlatform.Api

# 4. Run the API
dotnet run --project src/TicketingPlatform.Api
```

The API listens on `http://localhost:5000`. The OpenAPI document is at `http://localhost:5000/openapi/v1.json` in Development.

### Package versions

The `.csproj` pins `10.0.0` for the framework-aligned packages. If `dotnet restore` reports a version is not found, let the CLI pick the latest compatible version:

```bash
cd src/TicketingPlatform.Api
dotnet add package Microsoft.AspNetCore.OpenApi
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

## See multi-tenancy work (the point of Phase 1)

Open `requests.http` in Rider or VS Code (with the REST Client extension) and run the requests top to bottom. The walkthrough:

1. Create two tenants (Aurora Live, Vortex Events). Tenant creation needs no tenant header; tenants are the top-level owners.
2. Create an event under each tenant by sending a different `X-Tenant-Id` header.
3. List events as tenant A: you see only Aurora's event. List as tenant B: only Vortex's. The global query filter scopes every read with no `WHERE TenantId = ...` written by hand in the controller.
4. The kicker: try to GET tenant A's event by id while sending tenant B's header. You get 404, because the filter excludes it. Tenant B cannot even see that the row exists.

Watch the console while you do this. EF Core SQL logging is on (`Microsoft.EntityFrameworkCore.Database.Command` at Information), so you see the exact SQL, including the tenant predicate the filter injects.

## Code tour

- `Program.cs` — composition root: DI registration, middleware order, OpenAPI, exception handling.
- `Domain/` — the entities: `Tenant`, `Event`, `TicketType`, `Inventory`. Plain classes; no framework attributes (configuration lives in the DbContext).
- `Tenancy/TenantContext.cs` — `ITenantContext` (read) and `TenantContext` (scoped, holds the current tenant for the request).
- `Tenancy/TenantResolutionMiddleware.cs` — reads the `X-Tenant-Id` header into the tenant context. In Phase 3 this is replaced by the authenticated user's tenant claim.
- `Data/TicketingDbContext.cs` — entity configuration and the **global query filter**. `Tenant` has no filter; `Event`, `TicketType`, and `Inventory` are filtered by `TenantId == CurrentTenantId`.
- `Controllers/` — `TenantsController` (admin, unfiltered) and `EventsController` (tenant-scoped).
- `Common/` — `CorrelationIdMiddleware` and `GlobalExceptionHandler`.
- `Contracts/Dtos.cs` — request/response records. Mapping is explicit and manual on purpose (no AutoMapper).

## A few .NET details in this code worth understanding

- **Why a global query filter on a DbContext property works.** The filter lambda `e => e.TenantId == CurrentTenantId` references a property on the context instance. EF builds the model once but evaluates that property per query, so each request is scoped to its own tenant. The `DbContext` reads the tenant from the injected scoped `ITenantContext`.
- **Lifetimes.** `DbContext` and `TenantContext` are both scoped (one per request). Injecting a scoped service into the DbContext constructor is fine; injecting a scoped service into a singleton would be the classic captive-dependency bug.
- **`DateTimeOffset`, not `DateTime`.** Postgres `timestamp with time zone` plus `DateTime` is a common Npgsql trap (it rejects non-UTC `DateTime`). Using `DateTimeOffset` sidesteps it. Send ISO 8601 with an offset, for example `2026-07-01T20:00:00Z`.
- **`xmin` concurrency token.** `Inventory` uses Postgres's system `xmin` column as an optimistic-concurrency token (`UseXminAsConcurrencyToken`). It does nothing visible yet; it is the foundation for Phase 5, where concurrent buyers must not oversell.
- **ProblemDetails everywhere.** `AddProblemDetails` plus an `IExceptionHandler` means unhandled errors return a structured RFC 7807 body, not a raw stack trace. Phase 2 expands this into a full validation error contract.

## Deliberately not here yet (and where it arrives)

- Authentication and authorization, audit log: Phase 3 (the `X-Tenant-Id` header stands in for a tenant claim until then).
- FluentValidation and the full error contract: Phase 2.
- Clean Architecture layering: Phase 2 (you refactor this naive structure).
- Tests: Phase 2 (Week 4).
- Redis, RabbitMQ, sagas, background services, holds, the booking flow: Phases 4 and 5.

## Phase 1 exercises (cement the mechanics)

1. Add a `GET /api/events/{id}/ticket-types` endpoint. Confirm it is tenant-scoped without you writing a tenant predicate.
2. Add a `capacity` validation: reject a ticket type with `totalQuantity <= 0` (use `ModelState`/manual check for now; FluentValidation comes in Phase 2).
3. Change one read query to `AsNoTracking()` and observe in the logs that behavior is unchanged for reads, then explain why no-tracking is the right default for read endpoints.
4. Temporarily remove the global query filter on `Event` and rerun the walkthrough. Watch tenant isolation break. Put it back. That is the lesson.

## Next step

Phase 2 begins: validation with FluentValidation, the RFC 7807 error contract, the first tests, and the refactor to Clean Architecture. Ask when you want it.
