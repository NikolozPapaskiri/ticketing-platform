# Roadmap — what is next, and why

**Written:** 2026-07-27
**Verified against:** working tree at `1f6385d` (post `feature/observability-p5` merge)
**Companion doc:** [MULTI_TENANCY.md](MULTI_TENANCY.md) — the general shape of ticketing-as-a-service
and the tenancy design that drives Track 3 below.

This file supersedes the roadmap sections of
[TICKETING_PLATFORM_PRODUCT_RESEARCH.md](TICKETING_PLATFORM_PRODUCT_RESEARCH.md) (written
2026-07-11, before PRs 3–6 landed). That document remains the source for the competitor benchmark
and UX direction.

---

## 0. Where the code actually is

Claims below were checked against the source, not against the status blocks.

**Closed since the research doc was written** (the doc still lists these as open):

| Item | Evidence |
|---|---|
| Bounded outbox delivery (`NextAttemptAt`, backoff, `FailedAt`, quarantine, metrics) | `AddOutboxRetrySchedule` migration; fields on `Infrastructure/Outbox/OutboxMessage.cs` |
| Versioned event envelopes | `AddIntegrationEventEnvelopeMetadata` migration; typed `IIntegrationEvent` |
| Broker failure-window tests | messaging suite (unroutable / backoff / quarantine / disconnect / crash-redeliver) |
| Observability P1–P5 | `docs/OBSERVABILITY_PLAN.md` — alerts, Alertmanager, 5 dashboards, `k8s/monitoring/`, verified against a live stack |
| Distributed login limiter | `IDistributedRateLimiter` + Redis fixed window |

**Still genuinely open** — each verified by reading the code:

| # | Item | Evidence it is open |
|---|---|---|
| G1 | Refund-status inquiry | `IPaymentGateway` has `GetChargeStatusAsync` but **no** `GetRefundStatusAsync`. Refund recovery blind-retries `RefundAsync`. |
| G2 | Payment-lease extension is not concurrency-safe | `OrderService.FinalizeAsync:154` calls `ExtendPaymentLease` then plain `SaveChangesAsync`. The confirmed path (`:168`) correctly uses `TrySaveChangesAsync`; the ambiguous path does not. |
| G3 | Refund initiator not persisted | `Order` carries `ProviderRefundId` / `RefundedAt` / `RefundClaimedAt` — no initiating actor. The `OrderRefunded` event can therefore name `reconciler` as the actor. |
| G4 | Stale refund metadata | `Order.RevertRefundClaim()` (`Order.cs:79`) resets `Status` only; `RefundClaimedAt` keeps its value. |
| G5 | Reconcilers duplicate provider traffic | `OutboxDispatcher:198` claims rows with `FOR UPDATE SKIP LOCKED`; `GetOrderIdsWithStaleRefundClaimAsync` has no such claim, so every replica selects the same batch. |

Everything else on the backlog is new product or new platform capability, not unfinished safety work.

---

## Track 1 — Gate 0 — **G1–G4 DONE** *(branch `feature/gate0-money-safety`)*

Small, bounded, and it was the last thing standing between the current build and "money handling I
would defend in a review".

**Shipped:** **G1** refund-status inquiry — recovery asks the provider instead of re-issuing and
trusting the stable key to dedupe; `Refunded` settles from provider truth without moving money
again, `Pending` waits for the next scan, and `NotRefunded`/`Unknown` fall back to the previous keyed
retry so a provider without a status endpoint is no worse off. **G2** the `ProviderUnavailable` path
saves with `TrySaveChangesAsync` and reports whatever the winner settled, instead of throwing a 500
on the one path whose purpose is graceful degradation. **G3** `Order.RefundInitiatedByActor`,
recorded at claim time and carried into the `OrderRefunded` event so the reconciler cannot erase who
asked. **G4** `RevertRefundClaim` clears the claim metadata. Migration `AddRefundInitiator` (one
nullable column). 219 tests green (86 unit + 133 integration).

**Not built, deliberately:** G5 — per its own instruction below.

**G2's race test now exists and is proven** —
`PaymentRaceTests.AmbiguousPayment_LosingTheLeaseRace_Returns202_NotServerError`. It freezes the
request's own lease-extension save, has a competitor move the row with `ExecuteUpdateAsync`, releases,
and asserts 202. Verified to FAIL on the pre-G2 code (`Expected: Accepted / Actual:
InternalServerError`), so it pins the defect rather than merely passing. **Gate 0 is now closed for
G1–G4** (G5 remains deliberately unbuilt).

Getting there took two discarded attempts, and the reasons are worth not re-discovering:

1. `FaultInterceptor.PaymentLeaseExtendGate` now exists as the seam, and the detector has to
   distinguish an **extension** from the initial **claim**: both leave the hold `PaymentPending` with
   a modified lease, but the claim also writes `Status`. Gating on the claim by mistake makes the test
   exercise the "hold no longer available" 409 path instead of the ambiguous-payment one.
2. **The concurrency mechanism itself is proven reachable**, so the fault was in the HTTP
   orchestration rather than the model. `LeaseConcurrencyDiagnosticTests` pins it directly: load a
   `PaymentPending` hold, let another writer move the row with `ExecuteUpdateAsync`, extend the lease
   on the stale snapshot, and `SaveChangesAsync` throws `DbUpdateConcurrencyException`. That test is
   kept — it is the invariant G2's conflict branch depends on. It also asserts the lease value
   actually changed, because writing the value the row already holds produces no `UPDATE` and
   therefore no conflict, which is the trap to avoid when building the HTTP-level version.

   The unexplained part was why the HTTP attempt saw a gate arrival yet its save did not conflict.
   The likely answer is that the gate was **not scoped to a hold**, so it fired for whichever
   lease-extension save reached it first rather than the request's own. `PaymentLeaseExtendHoldId`
   now restricts it to one hold, which makes an arrival provably the save under test.

   That was the fix. With the gate keyed to the hold the test became meaningful, and reverting
   `TrySaveChangesAsync` to a plain save now makes it fail with the expected 500.

3. A third attempt looked like it proved the opposite ("the save never reaches the gate") but had
   actually died on `DockerUnavailableException` — the fixture never started. When a race test fails
   in ~2ms, read the stack before believing the symptom.

The gate's own wording — "the full integration suite green with Docker running, plus the new race
test" — is therefore satisfied for G1–G4.

### G1. Refund-status inquiry — *P1*
Add `GetRefundStatusAsync(refundIdempotencyKey)` returning `Refunded(providerRefundId) |
NotRefunded | Pending | Unknown`, mirroring the `PaymentInquiry` shape that already exists for
charges. Today refund recovery re-calls `RefundAsync` with the stable `refund:{orderId}` key and
trusts the provider to keep idempotency records for the whole recovery horizon — which is an
assumption about someone else's retention policy, not a guarantee. It also cannot distinguish
"refund completed, response lost" from "refund still processing" from "no refund exists". Make the
reconciler settle both money directions against provider truth.

### G2. Concurrency-safe lease extension — *P1*
Switch the `ProviderUnavailable` path to `TrySaveChangesAsync`; on conflict, clear tracking,
re-read the order, and return whatever the winner settled it to. Today a client retry and the
background reconciler can both load the same hold, both extend the lease, and one save throws
`DbUpdateConcurrencyException` — a 500 on a path whose entire purpose is to degrade gracefully.
Add a deterministic retry-versus-reconciler test using the existing `AsyncGate` /
`ControllablePaymentGateway` harness.

### G3. Persist the refund initiator — *P1*
Add `RefundInitiatedByActor` (and optionally `RefundCompletedByProcess` for diagnostics). Refunds
are money leaving; "who asked for this" is the first question in any dispute, and right now the
answer is lost whenever the reconciler finishes the job.

### G4. Clear stale refund metadata — *P2*
`RevertRefundClaim()` should null `RefundClaimedAt`. Not a live bug — the stale-refund query also
filters on status — but the entity currently carries a lie, and the next query written against that
column will inherit it.

### G5. Claim reconciliation batches — *P2*
Give the payment/refund reconcilers the same `FOR UPDATE SKIP LOCKED` claim the outbox dispatcher
already uses, plus scan jitter. Correctness is already protected by concurrency tokens and provider
idempotency; this is about not sending N replicas' worth of duplicate inquiries at a provider that
rate-limits. **Do not build this before the replica count or provider limits make it matter** —
noted here so the decision is deliberate rather than forgotten.

**Gate:** the full integration suite green with Docker running, plus the new race test.

---

## Track 2 — Product: what turns this into a ticketing *service*

Ordered by value. Each phase is independently shippable and independently demo-able.

### Phase A — Venue, performance, reserved seating — *the highest-value expansion*

**Slices 1-3 done.** The `Event → TicketType` model is now `Event → Performance → TicketType`.

**Slice 1** (`feature/phase-a-venue-geometry`): `Venue`, `Hall`, and immutably-versioned
seat maps (`SeatMapVersion` / `Section` / `SeatRow` / `Seat`), tenant-scoped like every other
operational entity, with seat-number-unique-per-row and version-unique-per-hall enforced in the
database. Additive only - nothing references them yet, so current behaviour is untouched. Migration
`AddVenueGeometry`.

**Slice 2** (`feature/phase-a-performance`) added **`Performance`** — the scheduled occurrence —
pinning the hall *and the seat-map version* it sells, with per-date cancellation that leaves sibling
dates selling. Also EXPAND-only: `Event.StartsAt` still drives general admission and every existing
event has zero performances, so behaviour is unchanged. Migration `AddPerformance`.

**Slice 3 is itself staged expand → migrate → contract; EXPAND and MIGRATE are done.**

**3a EXPAND** (`feature/phase-a-ticket-type-performance`): `TicketType.PerformanceId` exists as a
**nullable** column and is backfilled — every pre-existing event became a one-night run carrying its
own `StartsAt`. Reads were untouched, so behaviour was unchanged and the step shipped on its own.
The backfill SQL lives in `PerformanceBackfill` as constants so its tests execute *exactly* what the
migration executes; they pin idempotency (safe on rerun/partial failure/restore) and that an event
with real dates never gets a phantom synthetic one. Migration `LinkTicketTypeToPerformance`.

**3b MIGRATE** (`feature/phase-a-read-from-performance`): the date now comes from the date row.
Needed **no migration** — the expand step had already added the column, so this is pure code.

- *Writes stopped producing the old shape*, which is the precondition for 3c: creating an event
  creates its performance, adding a ticket type attaches it to one, and editing the event's date
  moves the performance rather than leaving it behind. An event-level date edit deliberately does
  **nothing** once an event has several dates — "the event moved to the 14th" has no meaning for a
  thirty-night run, and picking a night would silently move the wrong one.
- *Reads repointed* on all five surfaces that show a date: staff graph and list, marketplace catalog
  and event page, organizer storefront. The rule is **the earliest date still scheduled**, excluding
  cancelled ones, with the legacy column as fallback for rows that have no date row — that fallback
  is the only thing 3c has to delete. Catalog `from`/`to` filters moved with it.
- *Two questions, two answers.* `Event.HeadlineDate` summarises a run for a listing;
  `TicketType.AdmissionDate` names the one night a ticket admits you to. The ticket PDF now prints
  the second — with a run, the first is simply the wrong date, and a wrong date on a ticket is a
  customer turned away at the door.
- *Correction to this plan as written:* the availability projection needed **no** change. It carries
  ids and re-reads live inventory; it never touched `Event.StartsAt`. Adding `PerformanceId` to
  `EventAvailabilityView` would have been a column nothing reads, so it waits until something groups
  availability by date. Organizer UI and checkout needed no change either — the API contract is
  unchanged, only the *source* of the value moved.
- The tests drive the performance's date away from the legacy column **directly in the database**
  (the API cannot, since it keeps them in step) and then ask each surface what date the event is on.
  That divergence is the only way to tell which column was read. All were confirmed to fail against
  the pre-3b code.

**3c CONTRACT** (`feature/phase-a-contract-performance`): two migrations, in that order.

- `RequireTicketTypePerformance` re-runs the **same** backfill the expand step used — safe only
  because it was written idempotent, the payoff for that decision arriving a slice later — then sets
  `PerformanceId` NOT NULL. The scaffolded `AlterColumn` proposed an all-zeros default; removed, so
  `SET NOT NULL` fails loudly on a leftover row instead of attaching tickets to a date that does not
  exist. The invariant moves out of convention and into the schema.
- `DropEventStartsAt` removes the column and swaps the catalog index to `(Status, Category)` —
  ordering is served by `Performances (EventId, StartsAt)` now. Its `Down()` restores the **data**,
  not just the column: the scaffolded default parks every event at year 1, which a rollback would
  surface as a catalog of nonsense dates with nothing failing to say so.

**What the column was hiding.** A non-nullable date on the event meant every event *had* a date, so
an event with nothing scheduled — dates unannounced, or every night called off — was represented by
a date that was simply false. Null is the honest answer, and the two audiences want opposite things:

- **Staff** shapes expose it nullable; an organizer is exactly who needs to see "no dates scheduled",
  and those sort last in the list.
- **Public** shapes keep it required, because `PublicScope.OnSaleEvents()` now also requires a
  scheduled date. An event no one can buy a ticket to — a ticket type needs a performance — has no
  business in the catalog, and the buyer-facing projections then have no null to answer for.

**Two test casualties, both honest and worth knowing about.** `PerformanceBackfillTests` is gone:
its SQL reads `Events."StartsAt"`, so it cannot execute against the new schema at all — *a contract
step retires the tests of the migration it completes*. Its surviving guarantee became a constraint
test. And `PerformanceScheduleTests` asserted that an event with no performances still had a working
`Event.StartsAt`; that was the transitional promise this step exists to retire, so it now asserts
the opposite. The backfill SQL stays in `PerformanceBackfill` marked HISTORICAL — the two migrations
that precede the drop must keep executing exactly that text when a database is built from scratch.

Remaining, in order:

- **4** `PriceZone` + `Allocation`.
- **5** `SeatHold` with the partial unique index on `(performanceId, seatId)`, where the three
  reservation strategies collapse to "insert and let the constraint arbitrate" for reserved seating
  while GA keeps the counter model.

The current model is `Event → TicketType → Inventory`: a flat capacity counter, with `VenueName`
as a free-text string on `Event`. That is general admission only, and it cannot express what most
real ticketing is: a hall with a fixed geometry, sold repeatedly across dates at different prices.

Target model:

- **`Venue`** — physical location and address.
- **`Hall`** — an independently configurable space inside a venue.
- **`SeatMapVersion`** — an *immutable* version of a hall layout. Immutability matters: tickets
  sold against last season's map must keep rendering correctly after the hall is re-striped.
- **`Section` / `Row` / `Seat`** — seat identity (what is printed on the ticket) plus map
  coordinates (what is drawn in the UI). Keep those two concerns separate; renumbering a row must
  not invalidate sold tickets.
- **`Event`** — the reusable production/program definition (the show).
- **`Performance`** — one scheduled occurrence (the date). A theatre run is one event, thirty
  performances. Cloning the whole event per date causes content drift and destroys cross-date
  reporting.
- **`PriceZone`** — price assignment across seats or GA areas, per performance.
- **`Allocation`** — inventory carved out for online sale, box office, sponsors, guests, press,
  partners. Without this, organizers hold seats by "not publishing them", which is invisible and
  unreconcilable.
- **`SeatHold`** — seat-level temporary ownership, replacing counter decrement for reserved events.

**The invariant that must live in the database:** a partial unique index preventing two live
holds/orders from owning the same `(performanceId, seatId)`. Seat selection is the same contention
problem already solved for counters, but the unit of contention becomes a row rather than a number,
which actually makes it *easier* — a unique constraint is a stronger and cheaper guarantee than an
optimistic-token retry loop. Expect the three-strategy comparison from Phase 5 to collapse to
"insert and let the constraint arbitrate" for reserved seating, and keep the existing strategies
for GA. That contrast is itself a strong interview answer.

Cost to be honest about: this is the largest schema change in the project's history and it touches
holds, orders, tickets, availability projection, the read model, and the whole organizer UI.

### Phase B — Marketplace discovery

The catalog exists (categories, images, search, date filters). What is missing is everything that
makes a catalog *findable and trustworthy*:

- Localization (ka/en), city and venue filters, date shortcuts (today / this weekend / range),
  price and availability filters.
- Search across event, artist, venue, and organizer.
- Saved events and on-sale reminders — these also give you a demand signal before the on-sale,
  which is what lets you size the waiting room.
- Organizer profile pages; related-event recommendations.
- Explicit age, accessibility, refund, cancellation, and entry policies per event.
- Transparent fees: total price shown before payment, not at the last step.

Start with PostgreSQL full-text search plus `pg_trgm` — indexed similarity without a new service.
Add Meilisearch only when typo tolerance, facets, geo search, or search analytics are genuinely
needed. ([`pg_trgm` docs](https://www.postgresql.org/docs/18/pgtrgm.html),
[Meilisearch search API](https://www.meilisearch.com/docs/reference/api/search/search-with-get))

### Phase C — Organizer operating system

Where a ticketing platform actually earns its fee. Today an organizer can create events and read a
sales report; they cannot *run a business*.

- Multiple ticket tiers with sales windows (early bird → general → door).
- Promo codes: validity window, own inventory, audience restriction, usage limits.
- Affiliate / promoter tracking links with attribution.
- Complimentary tickets and invitation lists; manual reservations with expiry.
- Per-channel allocations (online / box office / partner).
- Event and performance cloning.
- Live dashboards: sales, revenue, occupancy, refunds, scan rate.
- Settlement and payout reports; CSV export.
- Granular staff roles: manager, event editor, box-office operator, scanner, finance viewer —
  the current single `OrganizerStaff` role is too coarse to hand to a venue's door staff.
- **Immutable audit history** for capacity, pricing, allocation, refund, and settlement changes.
  This is a compliance requirement, not a nice-to-have, the moment real money is involved.

### Phase D — Event-day operations

A dedicated scanner PWA. The web stack already covers this without native apps:

- Fast camera scanning; event/performance/section/gate validation.
- Unambiguous result states: accepted, duplicate, wrong event, void, refunded, not yet valid.
- **Offline encrypted manifest** with local duplicate detection and background sync. Venue wifi
  fails; the door cannot.
- Supervisor override with a reason and an audit record.
- Live admission counts per gate.

Later: replace static QR credentials with short-lived rotating signed codes. A static QR is
screenshot-able and therefore resellable; rotating codes are the industry answer.

### Phase E — Transfer and face-value return queue

Ticket transfer as a state machine: `Owned → TransferPending → Transferred | Cancelled`. Completing
a transfer must atomically change ownership, revoke the sender's credential, issue the recipient's,
and write the audit record — the same one-transaction discipline already used for checkout.

For sold-out events, a DICE-style return list beats an open resale market: the organizer enables
returns, a customer offers the ticket back, the next fan in line gets a short purchase window, the
replacement purchase completes, the original buyer is refunded. Tickets stay genuine, pricing stays
at face value, and the organizer gets demand data.

### Phase F — Flash-sale and anti-abuse hardening

The waiting room is already atomic (Lua admission), token-bucketed, and account-bindable. What is
left is abuse economics rather than mechanics:

- HMAC-signed join tokens and a join challenge (already noted as deferred).
- One active position per verified account per event; queue-abandonment handling; estimated wait.
- Purchase limits per account, verified phone, card fingerprint, and event.
- Bot challenge before joining high-demand queues.
- Metrics for admission rate, conversion, abandonment, queue age, rejected abuse.

At genuine public scale the queue belongs at the edge, in front of Next.js and the API, not inside
the origin — e.g. [Cloudflare Waiting Room](https://developers.cloudflare.com/waiting-room/about/),
which holds queue state at the edge and caps both total active users and new users per minute. The
in-app Redis queue stays the right choice for this project's scale and is the better teaching
artifact.

---

## Track 3 — Multi-tenancy maturity

Full reasoning in [MULTI_TENANCY.md](MULTI_TENANCY.md). The work items it produces:

### T1. Make the two planes explicit — **DONE** *(branch `feature/tenancy-access-scopes`)*

Shipped as five access scopes in `Infrastructure/Persistence/Scopes/AccessScopes.cs` — the only
file allowed to call `IgnoreQueryFilters`, enforced by an architecture test that was verified to
fail on a real violation. All 33 bypass sites moved onto it (the "38" in the original count
included 6 doc comments): 13 System, 7 Public, 6 Customer, 3 Platform, plus 3 named
`AuthorizedWriteCore` and 3 named `TenantDiscovery` for the sites that genuinely serve more than
one plane. `TenantScope` now fails closed. Ownership for customer reads — including the
transitive Order→Ticket join — is expressed once instead of per method. Zero behaviour change, no
migration; 214 tests green (83 unit + 131 integration) with no assertion edited. Public reads also
stopped returning entities - they project to rows in SQL. See MULTI_TENANCY.md §2.2 for the four
things implementation changed about the plan.

*Original description:*
Isolation today is one global query filter on nine entity types, defeated with
`IgnoreQueryFilters` wherever the customer plane needs cross-tenant reach (`OrderRepository` uses
it on essentially every method). Where the filter is bypassed, isolation depends on hand-written
predicates — which is exactly where leaks come from. Split the concept: a tenant-plane context
that fails closed, and a customer-plane context whose default-deny is *ownership*, enforced by one
audited mechanism rather than per-method care.

### T2. Per-tenant configuration entity
There is no `TenantSettings` anywhere in the codebase, and `Tenant` holds only `Name` and `Slug`.
Every real ticketing tenant needs: fee schedule, refund/cancellation policy, payout details,
branding, custom domain, locale and currency, tax treatment, waiting-room defaults, and per-tenant
limits.

### T3. Per-tenant merchant identity and settlement
`PaymentCharge` is `(IdempotencyKey, Amount, Currency)` — no merchant, no tenant. That hard-codes
"the platform is merchant of record for every sale". It is a legitimate model, but it is a
*decision*, and it determines who carries chargeback risk, who holds funds between sale and event,
and how organizers get paid. The alternative is connected accounts with split payments. Either way
the platform needs a settlement ledger and payout runs — see MULTI_TENANCY.md §5.

### T4. Noisy-neighbour isolation
The ticketing-specific tenancy problem: one tenant's on-sale must not degrade every other tenant.
Per-tenant quotas and rate limits, connection-pool bulkheads, and eventually cell-based isolation
for hot on-sales. See MULTI_TENANCY.md §4.

### T5. Tenant lifecycle
Provisioning, suspension, offboarding, per-tenant export and deletion (GDPR), and per-tenant
point-in-time restore. Single-tenant restore out of a shared schema is the real cost of the current
isolation model and should be prototyped before a customer asks for it.

---

## Track 4 — Platform and ops leftovers

- **CI hygiene:** Node-20 deprecation warnings on the GitHub Actions (known, non-fatal).
- **Merge the hardening branches.** PRs 1–6 are implemented and pushed but unmerged; the longer
  they sit off `main`, the more the merge costs.
- **Resource-based authorization handler** — still deferred from Phase 3. With customer-owned
  orders and tickets now real, `IAuthorizationService.AuthorizeAsync(user, resource, requirement)`
  finally has something to guard. Pairs naturally with T1.
- **Load-test the reserved-seating path** once Phase A lands; `tools/TicketingPlatform.LoadTest`
  already has the harness, and the seat-level contention profile will differ from the counter one.

---

## Recommended sequence

1. **Track 1 (Gate 0)** — one PR, closes money-handling safety. Do not start Phase A first.
2. **Track 3 / T1 + T2** — the tenancy split and the settings entity. Cheap now, expensive after
   Phase A doubles the number of tenant-scoped tables.
3. **Phase A** — venue / performance / reserved seating. The big one.
4. **Phase C**, then **Phase B** — organizer capability before discovery polish: an organizer who
   cannot run a promo code will not bring you events to discover.
5. **Phase D**, **T3/T4**, then **E** and **F** as the product earns them.

## What not to build

Worth stating explicitly, because restraint is the harder call:

- **Do not split into microservices.** The seams (outbox + broker) already exist; extraction stays
  cheap. Splitting now buys distributed failure modes and no independent scaling need. The one
  split already made — API vs worker roles from the same image — is the correct amount.
- **Do not add Elasticsearch/Meilisearch** until Postgres FTS + `pg_trgm` demonstrably falls short.
- **Do not build native scanner apps** until PWA scanning is proven insufficient.
- **Do not add per-tenant schemas or databases** on current volume. See MULTI_TENANCY.md §3 for the
  trigger conditions that would change this.
