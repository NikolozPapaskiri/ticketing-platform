# Frontend plan - Next.js web app over the ASP.NET Core API

Status: **implemented through M4**. This document is the plan of record and now also the
completion checklist for the frontend milestone.

## Goal

One production-grade web app that turns the platform into a usable product: a public
Ticketmaster-style storefront, a customer checkout, an organizer back office, and a platform
admin console, all backed by the existing .NET API. The frontend is the product demo layer,
not a second backend.

## Decisions

| Decision | Choice | Why / trade-off |
| --- | --- | --- |
| Framework | **Next.js App Router + React + TypeScript** | Public pages, authenticated portals, server rendering, and a strong React ecosystem. The .NET API stays the serious system. |
| One app vs four | **One app, four role-based areas** | Shared design system, auth, deploy, and tests. The API remains the real authorization wall. |
| Styling/components | **Tailwind CSS + shadcn-style owned components** | Fast to build, owned source, no opaque component dependency. |
| Server state | **TanStack Query** | Cache, retries, and invalidation for authenticated client flows. |
| Forms/validation | **Zod + React Hook Form** | Client validation improves UX; backend FluentValidation remains authoritative. |
| Auth transport | **HttpOnly cookie sessions via a Next.js BFF** | Route handlers call the API server-to-server and keep access/refresh tokens out of localStorage. |
| Real-time | **SignalR JS client, browser to API hub directly** | `/hubs/availability` is anonymous by design. REST calls still go through the BFF. |
| E2E tests | **Playwright golden journey** | Seed through the API, then drive the real browser flow. |
| Repo layout | **`apps/web` in this repo** | The product stays self-contained and CI owns both backend and frontend checks. |

```text
Browser
  -> Next.js UI (server and client components)
      -> Next.js route handlers (BFF): auth, refresh, proxying
          -> ASP.NET Core API
  -> /hubs/availability (SignalR, anonymous, CORS-scoped)
```

Storefront URL model: each organizer's public page lives at `/t/{slug}`. The homepage is a
tenant directory plus product entry point.

## Milestones

### M0 - Backend ready

- [x] 0.1 Verify the backend expansion: public browse, customer checkout, refund, ticket
  validation, build, and test suite.
- [x] 0.2 Add `/auth/me`, customer orders/holds list endpoints, `GET /public/tenants`, and
  hub-scoped CORS.
- [x] 0.3 Node.js/npm frontend prerequisite verified.

### M1 - Product demo: public storefront + customer flow

- [x] 1.1 Scaffold `apps/web` with Next.js, TypeScript, Tailwind, owned UI components, and
  TanStack Query.
- [x] 1.2 BFF auth route handlers for login/register/refresh/logout/me, HttpOnly cookies, and
  route guards for `/account`, `/organizer`, and `/admin`.
- [x] 1.3 Storefront landing, tenant directory, `/t/{slug}` event list, event detail, ticket
  types, and live availability through SignalR.
- [x] 1.4 Customer register/login, hold with TTL countdown, checkout, payment error states,
  My Orders, My Holds, ticket download, refund, and order status.

### M2 - Organizer portal (`/organizer`)

- [x] 2.1 Events table, create/edit, publish/close, and surfaced state-machine errors.
- [x] 2.2 Ticket types, inventory, and availability dashboard.
- [x] 2.3 Sales report from the vertical-slice endpoint.
- [x] 2.4 Ticket validation form calling `POST /tickets/validate`.
- [x] 2.5 Box office: staff holds, orders, lookup, refunds, and hold release.

### M3 - Platform admin portal (`/admin`)

- [x] 3.1 Tenants list and create.
- [x] 3.2 Staff/admin provisioning through `register-staff`.

### M4 - Hardening + integration

- [x] 4.1 Playwright golden journey and role-wall checks.
- [x] 4.2 CI web job for install, typecheck, lint, and production build.
- [x] 4.3 `web` service in docker-compose, plus README and architecture docs updated with the
  BFF layer.

## Implementation notes

- Public UI: `/`, `/t/{slug}`, `/t/{slug}/events/{eventId}`.
- Customer UI: `/account`, plus the checkout controls on event detail pages.
- Organizer UI: `/organizer` for event operations, ticket types, availability, sales report,
  ticket validation, and box office.
- Platform admin UI: `/admin` for tenant and staff provisioning.
- Playwright: `apps/web/tests/golden.spec.ts` seeds data through the API, then drives the
  customer purchase journey and role checks.
- Local HTTP runs set `COOKIE_SECURE=false`; real HTTPS production should leave secure cookies
  enabled or set `COOKIE_SECURE=true`.

## Future-session runbook

```bash
docker compose up -d postgres redis rabbitmq
dotnet run --project src/TicketingPlatform.Api

cd apps/web
npm.cmd install
npm.cmd run dev
```

- Open `http://localhost:3000`; do not use `http://127.0.0.1:3000` for Next dev or Playwright.
- API root `http://localhost:5000/` returns 404 by design. The OpenAPI JSON is
  `http://localhost:5000/openapi/v1.json`.
- Use `/admin` with `admin@platform.local` / `Admin123$` to create tenants and organizer staff.
- Use `/organizer` for staff workflows, `/account` for customers, and `/t/{slug}` for anonymous
  storefront testing.
- `npm.cmd run e2e` runs the golden journey. If a dev server is already running in PowerShell,
  use `$env:PLAYWRIGHT_SKIP_WEB_SERVER='1'; npm.cmd run e2e`.
- Verified at completion: backend 117 tests; frontend typecheck, lint, production build, npm
  audit, Playwright e2e, and docker-compose `web` image build.

## M5 - Marketplace upgrade (tkt.ge-inspired), implemented

Surveyed tkt.ge live: icon+label category navigation, date-first browsing, image-led cards,
one catalog across organizers. Translated to this product:

- [x] Backend: `Event.Category` (10 categories) + `Event.ImagePath`;
  `GET /api/v1/public/events` — anonymous CROSS-TENANT catalog of OnSale events with
  category/from/to/q(ILike)/tenantSlug filters and SQL-computed price-from;
  `GET /public/events/{id}` tenant-agnostic detail; staff image upload
  (`POST /events/{id}/image`, JPEG/PNG/WebP ≤2MB via IFileStorage) + anonymous
  `GET /public/events/{id}/image`. `AddEventCategoryAndImage` migration.
  123 backend tests green (6 new marketplace tests).
- [x] Homepage = marketplace: search-first hero (zero-JS GET form), icon category nav,
  "Happening soon" cross-tenant grid, organizers as a secondary section.
- [x] `/events` catalog: category chips with active state, date chips
  (today/tomorrow/weekend), search, pagination preserving filters.
- [x] `/events/{id}`: image hero (per-category gradient fallback), category badge, organizer
  link, same live-availability checkout panel. `/t/{slug}` storefronts unchanged.
- [x] Organizer portal: category select on the event form + marketplace image upload
  (new BFF multipart route `api/bff/events/[id]/image`).
- Known dev-only noise: React strict-mode double-mount aborts the first SignalR negotiation
  in `next dev` (retry connects); absent in production builds.
- Follow-ups if wanted: Playwright journey entering via the marketplace homepage; a seed
  script for demo data (this round seeded via API calls).

## Deliberately out of scope

Reserved seating, a real payment-provider UI, i18n, and native mobile remain out of scope for
this milestone.
