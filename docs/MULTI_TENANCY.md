# Ticketing as a service — the general solution, and multi-tenancy in it

**Written:** 2026-07-27
**Companion:** [ROADMAP.md](ROADMAP.md) (Track 3 turns this analysis into work items)

Two questions, answered in order:

1. What is the general shape of *any* product in this class — a platform that sells finite,
   contested, time-bound inventory on behalf of other businesses?
2. What does multi-tenancy actually mean in that shape, and where does the current implementation
   sit?

---

## 1. The general solution: what a ticketing platform really is

Strip the domain vocabulary away and every ticketing platform is the same four problems stacked:

**(a) A perishable, finite, contested inventory.** A seat at 20:00 on Friday is worth its price
until 20:01, then zero. It cannot be back-ordered, restocked, or oversold — an oversell is not a
stock-out, it is a person turned away at a door they paid to walk through. This is what separates
ticketing from ordinary e-commerce: normal retail can apologise and ship next week.

**(b) Demand that is not smooth.** Most inventory sells slowly; some inventory sells 80% of itself
in the first 90 seconds of an on-sale. The system must be sized for the spike or must shed load
deliberately. Every serious design decision in this class of product traces back to that one fact.

**(c) Money that belongs to someone else.** The platform charges the buyer but the revenue is the
organizer's, minus a fee. That makes it a marketplace, not a shop — with settlement, payouts,
chargeback liability, and tax treatment as first-class domain concerns rather than accounting
afterthoughts.

**(d) A physical redemption event.** The ticket must be validated at a door, often offline, often
by staff who do not work for you, in a 40-minute window where failure is visible to thousands of
people at once.

Everything else — catalog, search, branding, dashboards — is commodity SaaS. These four are the
domain.

### 1.1 The canonical domain model

The model that generalizes across GA, reserved seating, festivals, and cinema:

```
Tenant (organizer)
 └── Venue ── Hall ── SeatMapVersion ── Section ── Row ── Seat     [geometry, reusable, immutable per version]
 └── Event                                                        [the production: what it is]
      └── Performance                                             [the occurrence: when it is]
           ├── PriceZone      → price per seat class or GA pool
           ├── Allocation     → online / box office / guest / sponsor / partner
           └── Inventory      → counters (GA) or seat rows (reserved)
                └── Hold      → time-boxed claim, TTL, the contention primitive
                     └── Order ── Payment ── Ticket ── Credential  [the redemption artifact]
```

Two separations carry most of the weight:

- **Event vs Performance.** The show is not the date. A theatre run is one event and thirty
  performances with different prices, different availability, and shared content. Platforms that
  conflate them force organizers to clone events per date, which produces content drift and makes
  cross-date reporting impossible. *This project currently conflates them* — `Event` carries a
  single `StartsAt`.
- **Seat identity vs seat geometry.** What is printed on the ticket ("Balcony B, Row 4, Seat 12")
  and what is drawn on the map (x/y coordinates) must be separable, and layouts must be versioned
  immutably, or re-striping a hall silently rewrites tickets already sold.

### 1.2 The invariants worth building the system around

A ticketing platform is, formally, a small set of invariants defended under concurrency:

1. **Never sell inventory that does not exist.** Enforced at the database, never in application
   logic alone. For counters: an optimistic token or an atomic decrement. For seats: a partial
   unique index on `(performance, seat)` across live holds and orders — a constraint is a stronger
   and cheaper guarantee than a retry loop.
2. **Every unit of inventory is in exactly one state at a time**, and every transition is explicit
   and guarded. Free / Held / Sold / Released / Scanned / Refunded is a state machine, not a set of
   boolean columns.
3. **Money moves at most once per business operation.** Guaranteed by a *stable idempotency key
   derived from a persisted business fact* (the order id), never from an HTTP attempt. The claim
   must be committed *before* the external call, or a crash makes the charge unattributable.
4. **The provider is the authority on whether money moved.** Not your database. Recovery is
   "ask the provider what happened", both for charges and refunds.
5. **A credential admits exactly once.** Enforced by a compare-and-swap on the ticket, at the door,
   under bad network conditions.
6. **State changes and their published events commit atomically** — the outbox pattern, because a
   database write and a broker publish cannot be one transaction.

This project already implements 1, 2, 3, 5 and 6, and half of 4 (charges yes, refunds not yet —
see ROADMAP G1). That is the genuinely hard core, and it is the part most portfolio projects skip.

### 1.3 The load-shape response

Because of (b), a ticketing platform needs three defences that ordinary SaaS does not:

- **Load levelling at the front door** — a waiting room converting a spike into a controlled
  admission rate. Implemented here as a Redis sorted set with an atomic Lua admission and a
  per-event token bucket. At real public scale it belongs at the edge, ahead of the origin.
- **A read path that does not touch the contested write path** — availability served from a
  projection, not by counting live inventory. Implemented here as `EventAvailabilityView` fed by
  `AvailabilityChanged`.
- **Asynchronous everything that is not the purchase** — PDFs, emails, notifications, projections
  behind a broker, so a spike queues work instead of blocking checkout. Implemented.

The design rule this produces: **the only thing allowed to be slow during an on-sale is the thing
the customer is actively paying for.**

---

## 2. Multi-tenancy in ticketing is not ordinary multi-tenancy

Standard B2B SaaS tenancy is a *partition*: tenant A's users see tenant A's data, and no request
ever legitimately spans tenants. Ticketing breaks that model, and the break is the single most
important tenancy insight in this domain.

**A ticketing platform has two planes with different tenancy rules:**

| | Organizer plane | Customer plane |
|---|---|---|
| Who | organizer staff, box office, scanners | ticket buyers |
| Tenancy | belongs to exactly **one** tenant | belongs to **no** tenant; transacts with many |
| Default deny | tenant mismatch | **not the owner** |
| Typical query | "all orders for my event" | "all my orders, across every organizer I ever bought from" |
| Isolation mechanism | tenant predicate | ownership predicate |

A customer who buys from three organizers has one identity, one account page, one wallet of
tickets, and one login. Their order list is inherently cross-tenant. Meanwhile the marketplace
catalog is cross-tenant by definition — a global browse across every organizer's published events
is the *product*.

So the platform is B2B2C, and roughly a third of its read paths legitimately span tenants. Any
tenancy design that assumes "one filter, always applied" will be fought by the product itself.

### 2.1 How this project currently does it, and where it strains

The current model — verified in `TicketingDbContext` and the repositories:

- **Shared schema, discriminator column.** `TenantId` on nine entity types, each with
  `HasQueryFilter(e => e.TenantId == CurrentTenantId)`.
- **Tenant comes from the token.** `TenantResolutionMiddleware` reads a signed `tenant_id` claim.
  The `X-Tenant-Id` header was deliberately removed in Phase 3 — clients cannot choose their own
  tenant. This is the right call and the most commonly botched part of tenancy.
- **`Tenant` and `User` are deliberately unfiltered.** `Tenant` has no `TenantId` (platform admin
  must enumerate them); `User` is unfiltered because login precedes tenant resolution. Both are
  documented in place.
- **Cache keys are tenant-prefixed**, so the shared Redis cannot leak across tenants.
- **The customer and marketplace paths use `IgnoreQueryFilters`** — in `IEventRepository`'s
  marketplace methods, throughout `OrderRepository`, and in every background consumer (a background
  scope has no tenant).

The strain is visible in that last bullet. The global query filter is a genuinely good
default-deny for the organizer plane. But the customer plane and every background worker must
defeat it, and **once it is defeated, isolation depends entirely on whatever predicate the author
of that method remembered to write.** `OrderRepository` calls `IgnoreQueryFilters()` on
essentially every method; its safety rests on ownership checks in application code rather than on
a mechanism.

That is not a bug today. It is the shape from which tenancy bugs emerge later, because the safety
property is maintained by care rather than by construction, and care does not survive a codebase
growing a venue model, an allocation model, and thirty new query methods.

### 2.2 The fix: make the two planes explicit — **implemented (Track 3 / T1)**

Rather than one filter with escape hatches, model both planes as first-class access contexts:

- **`TenantScope`** — organizer plane. Fails closed: no tenant claim, no data. Exactly today's
  filter, but with `IgnoreQueryFilters` no longer reachable from tenant-plane repositories.
- **`CustomerScope`** — customer plane. Cross-tenant by design, default-denied by **ownership**:
  every query is rooted at `CustomerUserId`, enforced in one audited place rather than
  method-by-method.
- **`PublicScope`** — marketplace reads. Cross-tenant, but restricted to *published* state and
  projected DTOs. Never returns entities that could carry private fields.
- **`SystemScope`** — background workers. Explicitly tenant-less, deliberately privileged,
  and the only place `IgnoreQueryFilters` is allowed to live.

The payoff is that "which scope is this?" becomes a code-review question with four possible
answers and a compile-time home, instead of an invisible property of whether someone remembered a
`Where` clause. This is Track 3 / T1 in the roadmap, and it is worth doing *before* Phase A doubles
the number of tenant-scoped tables.

#### What shipped, and what the implementation changed about the plan

`Infrastructure/Persistence/Scopes/AccessScopes.cs` is now the only file in the solution that calls
`IgnoreQueryFilters`, enforced by an architecture test (verified to fail on a real violation). All
33 bypass call sites moved onto it. Four corrections to the sketch above, found while doing it:

1. **There are five scopes, not four.** `OpsSnapshotService` reads cross-tenant over HTTP as an
   authenticated **PlatformAdmin** — privileged, but with a real principal, so access is
   attributable. That is a different authorization story from a background worker, so it got its
   own `PlatformScope` rather than being folded into `SystemScope`.
2. **The customer plane escalates into the tenant plane.** The customer controllers call
   `ITenantContext.SetTenant` with a tenant they discover from the resource being touched
   (`GetHoldTenantIdAsync`, `GetTicketTypeSaleContextAsync`). So §2.1's "tenant comes from the
   token" is true for staff but not the whole story: for customers, tenant authority is *derived
   server-side from a client-supplied resource id*. Safe — the value is never taken from the client
   and ownership is still checked separately — but it is a second path to tenant authority and it
   is why those lookups exist. They are now named `SystemScope.TenantDiscovery`, and they may only
   project a tenant id, never an entity.
3. **Three methods serve all three planes.** Checkout finalize, refund, and the idempotency lookup
   are entered from customer, organizer, *and* the reconciler. Because of (2) a tenant is already
   established on the first two, so their filter bypass exists **solely for the reconciler**. They
   are named `SystemScope.AuthorizedWriteCore` — the name is the reminder that authorization
   happened upstream.
4. **One genuine finding, deliberately not fixed.** `GetImagePathAsync` has no `OnSale` predicate,
   so the image path of a *draft* event is reachable by id — every other public read restricts to
   published. Fixing it is a behaviour change, so T1 (a zero-behaviour-change refactor) preserved
   it behind the deliberately uncomfortable name `PublicScope.EventsIncludingUnpublished`. Worth a
   follow-up decision.

`TenantScope` also now **fails closed**: previously a missing tenant made the filter compare
`TenantId == null`, match nothing, and return an empty result indistinguishable from "no data" —
the worst kind of isolation failure because it is silent.

---

## 3. Isolation strategies, and when to change

| Strategy | Isolation | Cost | Right when |
|---|---|---|---|
| **Shared schema + discriminator** *(current)* | Weakest — one missing predicate leaks | Cheapest: one migration, one pool, one backup | Many small/medium tenants, uniform features, no per-tenant compliance |
| **Schema per tenant** | Stronger — separate namespaces, per-tenant restore is real | Migrations × N; connection/search-path management; hundreds of schemas strain tooling | Tens-to-low-hundreds of tenants, per-tenant restore or export required |
| **Database per tenant** | Strongest — physical isolation, per-tenant tuning, residency | Highest ops cost; cross-tenant marketplace queries become a fan-out or a separate read store | Few large tenants, contractual/regulatory isolation, data residency |
| **Hybrid: pooled by default, silo for the largest** | Per-tenant choice | Two code paths in provisioning | The realistic end state of most successful SaaS |

Shared schema is the correct choice here and should stay. The triggers that would change it, in
order of likelihood:

1. **A customer demands per-tenant point-in-time restore.** Restoring one tenant out of a shared
   schema means selective row-level replay — expensive to build, terrifying to run. This is
   usually the first real forcing function.
2. **Data residency.** "Our data stays in the EU" cannot be satisfied by a row predicate.
3. **A single tenant large enough that its load profile justifies its own database** — which in
   ticketing means a tenant whose on-sales are big enough to need their own capacity anyway.
4. **Per-tenant compliance audits** demanding demonstrable physical separation.

Note the asymmetry that makes the marketplace matter: silo-per-tenant makes *isolation* easy and
*discovery* hard, because the global catalog then needs a cross-database read model. If the
platform ever silos large tenants, the marketplace must already be served from a projection rather
than from live tenant tables. Which is an argument for building the read models *before* the
isolation change — and they largely exist.

---

## 4. The ticketing-specific tenancy problem: noisy neighbours with spikes

Generic multi-tenancy writing assumes tenants generate smooth, comparable load. Ticketing tenants
do not. One tenant's on-sale can generate more traffic in 60 seconds than every other tenant
generates that week.

**The failure mode:** tenant A's on-sale saturates the connection pool, the Redis instance, the
broker, and the worker pool. Tenant B — running an ordinary Tuesday — sees timeouts on a browse
page. Tenant B did nothing wrong and will still churn.

Defences, cheapest first:

1. **Per-tenant quotas and rate limits.** The distributed limiter already exists for auth; extend
   it to per-tenant request and hold budgets. A tenant can exhaust its own budget, not the
   platform's.
2. **Bulkheads.** Partition the connection pool and worker concurrency so no single tenant can
   consume all of either. A bounded queue per tenant with fair scheduling beats one global queue.
3. **Waiting room as a tenant-level valve.** Already implemented per-event; the point to make
   explicit is that it protects *other tenants*, not just the origin. That reframing makes it a
   tenancy feature rather than a UX feature.
4. **Priority classes.** Checkout for a paying customer outranks catalog browse. Under saturation,
   shed browse first — it is served from a projection and can be stale or cached.
5. **Cell-based architecture with shuffle sharding.** The end state. Partition tenants across
   independent cells (app + database + Redis + broker); a cell failure or saturation event affects
   only its tenants. Ticketing suits cells unusually well, because **the contested resource never
   spans tenants** — no seat is fought over by two organizers, so there is no cross-cell
   coordination in the hard path. Only the marketplace catalog is global, and it is already a
   projection, so it can be fed from all cells into one read store.
6. **Scheduled on-sale capacity.** Organizers announce on-sale times. Knowing the spike's timing in
   advance is a luxury most systems do not have — use it: pre-scale, pre-warm caches, and
   optionally move a large on-sale into a dedicated cell for the day.

That last point is the difference between a ticketing platform and a generic high-traffic system.
**The spikes are scheduled.** A design that does not exploit that is leaving its cheapest win on
the table.

---

## 5. The money model is a tenancy decision

`PaymentCharge` is currently `(IdempotencyKey, Amount, Currency)` — no merchant, no tenant. That
encodes a real architectural position: **the platform is merchant of record for every sale.**

That is a legitimate model, and it should be a stated decision rather than an accident of the
port's signature. The two options:

| | Platform as merchant of record *(current)* | Connected accounts / split payments |
|---|---|---|
| Money lands in | platform account, paid out later | organizer's own account, fee split at charge time |
| Chargeback liability | the platform | mostly the organizer |
| Regulatory weight | heavy — the platform handles others' funds | lighter |
| Organizer onboarding | simple | KYC per organizer |
| Refund mechanics | platform controls, can refund from float | constrained by the organizer's balance |

Either way, a ticketing platform needs three things this project does not yet have:

- **A settlement ledger.** Immutable, double-entry-shaped records of every sale, fee, refund,
  chargeback, and payout per tenant. Do not compute settlement by aggregating orders at report
  time; orders mutate, ledgers do not.
- **Payout runs** with a hold period. Funds are typically held until after the event, because a
  cancelled event means mass refunds and an organizer who has already been paid and spent the
  money is the platform's problem. This is the single biggest financial risk in the business, and
  it is a *tenancy* concern: the hold policy is per-tenant, negotiated by contract.
- **Per-tenant fee schedules** — percentage, fixed, per-ticket, absorbed vs passed to the buyer,
  which is also what "transparent fees" in the checkout UI depends on.

This is Track 3 / T3, and it is the item that most distinguishes a ticketing *service* from a
ticketing *application*.

---

## 6. Per-tenant configuration: the missing entity

There is no `TenantSettings` type in the codebase; `Tenant` carries `Name` and `Slug`. Every real
tenant needs at minimum:

- **Commercial:** fee schedule, currency, payout details and schedule, tax/VAT treatment.
- **Policy:** refund and cancellation rules, transfer permitted, age restrictions, purchase limits.
- **Brand:** logo, colours, custom domain or subdomain, email sender identity, ticket template.
- **Operational:** hold TTL, waiting-room defaults (batch size, admission rate), allocation
  defaults, scanner gate configuration.
- **Localization:** locale, timezone (an organizer's "today" is their timezone, not the server's —
  a real source of off-by-one-day bugs in event listings).

Two design notes worth stating up front:

- **Settings are versioned, not overwritten.** An order placed under last month's refund policy is
  governed by last month's refund policy. Store the policy version on the order. This is the same
  immutability argument as seat-map versions, and it is the kind of thing that is nearly impossible
  to retrofit after a dispute.
- **Custom domains change the tenant-resolution story.** Today the tenant comes from a token claim,
  which is correct for authenticated traffic. A per-tenant storefront domain means *anonymous*
  traffic also carries tenant identity — resolved from the host header, cached, and never trusted
  for anything but content selection. Keep the rule: host may select a *storefront*, only the token
  may grant *authority*.

---

## 7. Tenant lifecycle

The least glamorous track and the one that becomes urgent without warning:

- **Provisioning** — self-serve signup, trial, plan limits, first-event onboarding.
- **Suspension** — non-payment or abuse. What happens to in-flight orders and already-sold tickets
  when a tenant is suspended? Buyers hold valid contracts; the answer cannot be "their tickets stop
  working".
- **Offboarding** — full export of the tenant's events, orders, customers, and settlement history
  in a usable format. Contractually common, and cheap only if designed for.
- **Deletion** — GDPR erasure that must not destroy financial records the platform is legally
  required to retain. The resolution is usually pseudonymisation of personal data with the ledger
  intact, which is a schema decision made *before* the request arrives.
- **Per-tenant restore** — see §3. The hardest one under shared schema and the most likely trigger
  for changing isolation strategy.

---

## 8. Summary judgement

The architecture is right for what it is. Shared-schema tenancy with a token-derived tenant claim,
a global query filter, tenant-prefixed cache keys, and a cross-tenant marketplace fed by
projections is the correct default for a platform of this size, and the hard core — contention
control, durable payment state, idempotent recovery, at-least-once delivery with an outbox — is
already built and tested.

Three things separate it from a production ticketing service, in order:

1. **The two-plane tenancy model should be explicit** rather than maintained by remembering to
   write the right predicate after `IgnoreQueryFilters`. Cheap now; expensive after Phase A.
2. **Venue / performance / seating** is the domain model the product is missing, and the
   `Event`-vs-`Performance` split is the part that cannot be retrofitted cheaply.
3. **The money model — merchant of record, ledger, payouts, per-tenant fees — is a tenancy
   decision that has been made implicitly** by the shape of `PaymentCharge`, and it deserves to be
   made on purpose.

Noisy-neighbour isolation and cells matter, but only at a scale this platform has not reached.
Naming the trigger conditions now (§3, §4) is worth more than building for them.
