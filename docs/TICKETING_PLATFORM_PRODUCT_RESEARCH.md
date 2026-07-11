# Ticketing Platform: Hardening Audit and Product Research

**Research date:** 2026-07-11  
**Code baseline:** `feature/rabbitmq-delivery-safety` at `d3648ab`  
**Comparison baseline:** `main` at `37c30f6`

## Executive summary

The platform has moved materially closer to production safety. The recent hardening work closes the original payment/hold-expiry, duplicate checkout, concurrent refund, duplicate scan, repeated inventory release, and unconfirmed RabbitMQ publication defects.

The strongest parts are the explicit state machines, stable provider idempotency keys, persisted claims before external money movement, optimistic concurrency tokens, provider charge reconciliation, publisher confirms, mandatory RabbitMQ routing, topology initialization, and deterministic race tests.

It is not finished. The immediate priority remains completing the safety gate before starting a large product feature. The most important remaining issues are concurrency-safe payment-lease extension, provider-truth refund reconciliation, preservation of the original refund actor, bounded outbox retries, and broader broker failure-window testing.

After that safety gate, the highest-value product expansion is a proper venue/performance/reserved-seating model. This unlocks the product capabilities expected from platforms such as TKT.GE: reusable venue plans, seat-level inventory, repeated performances, pricing zones, allocations, invitations, box-office operations, and event-day access control.

## Scope and verification

The review covered all ten commits between `main` and the current branch:

1. `3782945` - production-safety hardening plan
2. `a462297` - deterministic payment-race tests
3. `b3a20f1` - durable `PaymentPending` state and reconciliation
4. `6723806` - automatic crashed-checkout recovery test
5. `5669766` - PR 1 documentation
6. `a0a65b4` - deterministic refund/scan/release race tests
7. `b1bdd09` - atomic refund, scan, and hold release
8. `4fb3e5e` - scanned-ticket refund policy
9. `1f2fab4` - refund reconciler and reservation-strategy release tests
10. `d3648ab` - RabbitMQ publisher confirms and topology-before-dispatch

Total delta: 46 files changed, 4,949 insertions, and 147 deletions.

Verification performed during this review:

- Release solution build passed with zero warnings and zero errors.
- All 60 unit tests passed.
- Integration tests could not start because Docker Desktop was not running. The failures were Testcontainers fixture-construction failures, not failed application assertions.
- The working tree was clean before this document was added.

## Recent hardening changes

### Durable payment workflow

Checkout now:

1. Validates the hold and request.
2. Creates a stable order ID.
3. Moves the hold from `Active` to `PaymentPending` with a payment lease.
4. Persists the order, hold claim, and idempotency record before calling the provider.
5. Uses the persisted order ID as the provider idempotency key.
6. Finalizes the result in a second database transaction.
7. Returns a pending response for ambiguous provider outcomes.
8. Reconciles a stranded payment by asking the provider for the charge status.

The partial unique order index permits only one live purchase for a hold. `Hold` and `Order` now carry PostgreSQL `xmin` concurrency tokens.

This closes two serious defects:

- Two concurrent checkout requests can no longer independently charge the same hold.
- Hold expiry cannot release inventory while the hold is durably claimed for payment.

The implementation is in [OrderService.cs](../src/TicketingPlatform.Application/Services/OrderService.cs), [Hold.cs](../src/TicketingPlatform.Domain/Hold.cs), [Order.cs](../src/TicketingPlatform.Domain/Order.cs), and [TicketingDbContext.cs](../src/TicketingPlatform.Infrastructure/Persistence/TicketingDbContext.cs).

### Atomic refund, scan, and release

Refund now uses an order transition from `Confirmed` to `RefundPending` before calling the provider. The stable provider key is `refund:{orderId}` regardless of whether the request came from a customer, staff member, retry, or reconciler.

Ticket scanning is protected by a ticket concurrency token, so two scanners cannot both complete the `Valid` to `Scanned` transition.

Hold release credits inventory only once under concurrent release attempts. The behavior is tested across optimistic, pessimistic, and Redis-atomic reservation strategies.

The selected business policy is explicit: a scanned ticket is non-refundable and the API returns a conflict response.

### RabbitMQ delivery safety

The outbox dispatcher now publishes through a confirm-enabled channel and uses `mandatory: true`. An outbox record is marked processed only after RabbitMQ acknowledges the message. An unroutable message raises a publication exception and remains pending.

Broker topology is centralized in [RabbitMqTopology.cs](../src/TicketingPlatform.Infrastructure/Messaging/RabbitMqTopology.cs) and initialized before dispatchers and consumers start. The topology includes the missing `OrderRefunded` notification binding.

These changes correctly establish at-least-once delivery:

- The system may republish after a crash between broker confirmation and `ProcessedAt` persistence.
- It should not silently lose a message merely because the client publication method returned.
- Consumers must therefore remain idempotent.

## Strong engineering decisions

### Deterministic concurrency testing

The new tests coordinate exact failure windows using gated providers and concurrent tasks. This is much more valuable than repeatedly launching random requests and hoping a race appears.

The tests now demonstrate:

- Checkout crossing the hold-expiry boundary.
- Duplicate checkout against one hold.
- Crash after provider success but before local finalization.
- Automatic reconciliation without a client retry.
- Concurrent customer and staff refunds.
- Concurrent ticket scans.
- Concurrent releases across every inventory strategy.
- Unroutable outbox publication remaining unprocessed.

### Stable identities for external side effects

The provider key is derived from a persisted business operation, not from an individual HTTP attempt. This is the correct model for recoverable external writes.

### Explicit domain states

`PaymentPending` and `RefundPending` make uncertainty visible in the domain. A network timeout is no longer incorrectly interpreted as “the payment did not happen.”

### Modular monolith remains the correct architecture

The project already has clear domain, application, infrastructure, API, background-worker, and frontend boundaries. Splitting it into services now would increase distributed failure modes and deployment complexity without providing an independent scaling or ownership benefit.

## Remaining engineering improvements

### P1: make payment-lease extension concurrency-safe

The ambiguous payment paths extend a concurrency-protected `Hold` lease and call plain `SaveChangesAsync`. A client retry and a background reconciler can both load the same hold, extend it, and cause one save to throw `DbUpdateConcurrencyException`.

Affected areas:

- `OrderService.FinalizeAsync`, provider-unavailable path
- `OrderService.ApplyInquiryAsync`, pending/unknown path

Recommended change:

1. Use `TrySaveChangesAsync` for lease extension.
2. On a concurrency conflict, clear tracking and re-read the order.
3. Return the already-settled state when another worker won.
4. Add a deterministic retry-versus-reconciler test.

### P1: add refund-status inquiry

Charge recovery asks the provider for charge status. Refund recovery only calls `RefundAsync` again with the stable key.

That is safe only if the provider retains refund idempotency records for the complete recovery horizon. It also cannot distinguish:

- Refund completed but the response was lost.
- Refund still processing.
- Provider has no refund for the key.
- Provider inquiry is temporarily unavailable.

Recommended gateway contract:

```text
GetRefundStatusAsync(refundIdempotencyKey)
  -> Refunded(providerRefundId)
  -> NotRefunded
  -> Pending
  -> Unknown
```

The state machine should then reconcile both payment directions against provider truth.

### P1: preserve the initiating refund actor

The order persists `RefundClaimedAt`, but not the customer or staff actor that initiated the refund. If the reconciler completes the operation, the `OrderRefunded` event records `reconciler` as the actor even though the event comment claims it records the initiating actor.

Persist at least:

- `RefundInitiatedByActor`
- `RefundClaimedAt`
- `RefundedAt`
- `ProviderRefundId`

Optionally distinguish `RefundCompletedByProcess` for operational diagnostics.

### P2: clear stale refund metadata

`RevertRefundClaim()` changes `RefundPending` back to `Confirmed` but does not clear `RefundClaimedAt`. The current stale-refund query also filters by status, so it is not an immediate correctness failure, but the entity retains misleading state.

### P2: reduce duplicate reconciliation traffic

Multiple replicas can select the same stale payment or refund IDs. Database concurrency and provider idempotency protect correctness, but replicas can duplicate provider calls.

Possible improvements:

- Add a short reconciliation lease and lease owner.
- Atomically claim a batch using `FOR UPDATE SKIP LOCKED`.
- Add jitter to scan intervals.
- Record inquiry attempt count and next inquiry time.

Do not add this complexity solely for elegance. Add it when provider traffic, rate limits, or replica count make duplicate inquiries material.

### P2: complete bounded outbox delivery

Publisher confirms solve broker ownership, but permanent failures currently retry forever.

Add:

- `NextAttemptAt`
- Exponential backoff with jitter
- Maximum attempt or maximum age policy
- `FailedAt` and failure reason
- Operator-visible quarantine/replay workflow
- Metrics for pending age, failed rows, retry rate, returns, and confirmation latency

Missing high-value tests:

- Connection loss before broker confirmation
- Broker negative acknowledgment
- Confirmed publication followed by process death before `ProcessedAt`
- Topology initialization before the first dispatch
- Consumer duplicate after republishing

### P3: version event envelopes

Outbox event payloads should have a versioned envelope before external consumers appear:

```json
{
  "messageId": "...",
  "eventType": "OrderConfirmed",
  "schemaVersion": 1,
  "occurredAt": "...",
  "tenantId": "...",
  "correlationId": "...",
  "payload": {}
}
```

Avoid embedding .NET assembly-qualified types or treating internal entity JSON as a public contract.

## TKT.GE product benchmark

TKT.GE demonstrates that a mature regional ticketing platform is not merely an event catalog with card checkout.

### Customer-facing capabilities

TKT.GE currently presents:

- Categories covering music, cinema, railway, transport, theatre, opera, sport, festivals, children, conferences, tourism, museums, and other activities.
- Interactive venue and stadium seat maps.
- Card payment.
- Tickets delivered to the customer account and email.
- Mobile QR admission without printing.
- Apple Wallet and Google Wallet storage.
- A virtual queue for high-demand events with queue position and estimated wait time.
- Event-specific rules for children, admission, postponement, cancellation, and refunds.

Sources:

- [TKT.GE marketplace](https://tkt.ge/en?lang=en)
- [TKT.GE sports purchasing and queue flow](https://tkt.ge/en/sport)

### Organizer-facing capabilities

TKT.GE markets a broader operational service:

- Electronic sales through the marketplace.
- Physical box-office sales from the same inventory.
- Electronic plans for new venues and coordinated seats.
- Branded physical tickets and invitations.
- Temporary box offices, POS equipment, and cashiers.
- Account-manager support for reservations, pricing, invitations, statistics, and event-day planning.
- An organizer administration portal with real-time statistics, bookings, and analytics.
- Event-day controllers, turnstile integration, and handheld scanners.
- Promo codes, cross-selling, and marketing services.

Source: [TKT.GE services for organizers](https://tkt.ge/en/fororganizations)

### Lessons to copy, not necessarily visuals

The key TKT.GE lesson is product completeness:

- One inventory must serve online checkout, organizers, invitations, and box offices.
- Venue configuration is a first-class domain.
- Event-day access control is part of the product.
- High-demand queueing must be understandable to customers.
- Regional platforms need local language, currency, support, and organizer workflows.

The goal should not be a visual clone. The project should retain its own design system while matching this operational depth.

## Comparable product research

### Eventbrite

Eventbrite combines event setup with organizer check-in, real-time sales, attendance tracking, on-site sales, marketing, and reporting.

Product lesson: organizer value comes from the complete event lifecycle, not just ticket creation.

Sources:

- [Eventbrite organizer check-in application](https://www.eventbrite.com/organizer/features/organizer-check-in-app/)
- [Eventbrite organizer features and pricing](https://www.eventbrite.com/organizer/pricing/)

### DICE

DICE allows fans to return tickets to an event waiting list. The next fan buys through the platform rather than through an uncontrolled secondary market. Organizers can see unfulfilled demand and use it to plan additional dates.

Product lesson: a face-value return queue is a safer and simpler first step than an unrestricted resale marketplace.

Source: [DICE waiting-list model](https://dice.fm/blog/identify-fan-demand-with-the-waiting-list?lng=en)

### AXS

AXS Mobile ID uses a changing QR code, prevents screenshot-based copying, supports controlled transfer, invalidates the sender's credential after transfer, and supports organizer-controlled official resale.

Product lesson: ticket ownership and the admission credential must be stateful. Transfer is a revocation-and-reissue workflow, not emailing the same PDF to another person.

Sources:

- [AXS Mobile ID and rotating QR](https://support.axs.com/hc/en-us/articles/201086794-Do-I-need-the-AXS-App-to-use-my-AXS-Mobile-ID)
- [AXS screenshot protection](https://support.axs.com/hc/en-us/articles/360031506054-Can-I-share-screen-shots-of-digital-tickets-with-friends)
- [AXS completed-transfer invalidation](https://support.axs.com/hc/en-us/articles/201086764-Can-I-still-use-a-ticket-after-I-have-transferred-or-shared-it-with-someone-else)
- [AXS official resale](https://support.axs.com/hc/en-us/articles/360031622634-How-do-I-sell-my-tickets)

## Capability comparison

| Capability | Current project | Market expectation | Priority |
|---|---|---|---|
| Cross-tenant marketplace | Implemented | Core | Done |
| Customer checkout and account | Implemented | Core | Done |
| Hold TTL and oversell prevention | Implemented with three strategies | Core correctness | Done |
| Durable payment recovery | Implemented, remaining lease race | Core correctness | P1 |
| Atomic refund | Implemented, refund inquiry missing | Core correctness | P1 |
| QR ticket and scan | Implemented, static credential | Core | Improve |
| Real-time availability | Implemented with SignalR | Expected for scarce inventory | Done |
| Flash-sale waiting room | Implemented, abuse hardening incomplete | High-demand requirement | P1/P2 |
| Organizer portal | Implemented baseline | Needs operational depth | P1 product |
| Venue and reserved seating | Not implemented | Expected for sport/theatre/concerts | P1 product |
| Repeated performances | Not first-class | Expected for cinema/theatre | P1 product |
| Promo codes and invitations | Not implemented | Common organizer requirement | P1 product |
| Physical/box-office channel | Not implemented | Important regional/venue feature | P2 |
| Offline scanner | Not implemented | Important event-day capability | P2 |
| Ticket transfer | Not implemented | Common customer feature | P2 |
| Face-value return waiting list | Not implemented | Strong differentiator | P2 |
| Wallet passes | Not implemented | Mobile convenience | P2 |
| Search with typo tolerance/facets | Basic catalog filters | Marketplace expectation | P1/P2 |
| Marketing and notifications | Limited | Organizer growth feature | P2 |
| Payouts and settlements | Limited reporting | B2B requirement | P2 |

## Recommended product roadmap

### Gate 0: complete production safety

Before adding a large domain feature:

1. Make payment-lease extension concurrency-safe.
2. Add refund-status inquiry.
3. Persist the refund initiator.
4. Add bounded outbox retries and quarantine.
5. Version event envelopes.
6. Add broker failure-window tests.
7. Start Docker and run the full integration suite.
8. Update the hardening plan and `AGENTS.md` status.

### Phase A: venue, performance, and reserved seating

This is the highest-value product expansion.

Recommended domain model:

- `Venue`: physical location and address
- `Hall`: independently configurable space inside a venue
- `SeatMapVersion`: immutable version of a hall layout
- `Section`: balcony, floor, stand, VIP, and similar areas
- `Row`
- `Seat`: semantic seat identity and map coordinates
- `Event`: reusable production or program definition
- `Performance`: one scheduled occurrence of an event
- `PriceZone`: price assignment for seats or general-admission areas
- `Allocation`: inventory reserved for online sale, box office, sponsors, guests, or partners
- `SeatHold`: seat-level temporary ownership

The `Event` and `Performance` distinction is important. A theatre production may have many dates with different prices and availability. Duplicating the complete event for every performance creates content drift and weak reporting.

Reserved-seat uniqueness must ultimately be enforced in the database. A partial unique constraint should prevent two live holds/orders from owning the same performance seat.

### Phase B: marketplace discovery

Add:

- Georgian and English localization
- City and venue filters
- Date shortcuts such as today, weekend, and selected range
- Category and price filters
- Availability state
- Search by event, artist, venue, and organizer
- Saved events and on-sale reminders
- Related-event recommendations
- Organizer profile pages
- Explicit age, accessibility, refund, cancellation, and entry policies
- Transparent fee and total-price presentation before payment

Start with PostgreSQL full-text search and the `pg_trgm` extension. It supports indexed similarity matching without adding another service.

Source: [PostgreSQL `pg_trgm` documentation](https://www.postgresql.org/docs/18/pgtrgm.html)

Introduce Meilisearch only when the product needs stronger typo tolerance, facets, geographical search, independent ranking, or search analytics. It supports prefix search, typo tolerance, filtering, facets, sorting, and geographic queries.

Source: [Meilisearch search API](https://www.meilisearch.com/docs/reference/api/search/search-with-get)

### Phase C: organizer operating system

Add:

- Multiple ticket tiers and sales windows
- Promo codes with validity, inventory, audience, and usage limits
- Affiliate and promoter tracking links
- Complimentary tickets and invitation lists
- Manual reservations with expiry
- Per-channel allocations
- Event and performance cloning
- Real-time sales, revenue, occupancy, refund, and scan dashboards
- Settlement and payout reports
- CSV exports
- Staff roles: manager, event editor, box-office operator, scanner, finance viewer
- Immutable audit history for capacity, pricing, allocation, refund, and settlement changes

### Phase D: event-day operations

Build a dedicated scanner progressive web application:

- Fast camera scanning
- Event, performance, section, and gate validation
- Clear accepted, duplicate, wrong-event, void, refunded, and not-yet-valid states
- Offline encrypted event manifest
- Local duplicate detection
- Background synchronization after connectivity returns
- Supervisor override with reason and audit record
- Live admission counts per gate

Later, replace static QR credentials with short-lived signed or rotating codes. Avoid requiring native iOS and Android applications until platform usage justifies maintaining both.

### Phase E: transfer and face-value return queue

Ticket transfer should be a state machine:

```text
Owned -> TransferPending -> Transferred
                  |-> Cancelled
```

Completing a transfer must atomically:

1. Change ticket ownership.
2. Revoke the sender's credential.
3. Issue a new credential for the recipient.
4. Record the audit event.

For sold-out events, start with a DICE-style return waiting list:

1. The organizer enables returns for the event.
2. A customer offers a ticket back.
3. The next eligible fan receives a short purchase window.
4. A replacement purchase completes.
5. The original customer is refunded.

This keeps tickets genuine and can enforce face-value pricing while providing organizers with demand data.

### Phase F: flash-sale and anti-abuse hardening

Improve the current Redis waiting room with:

- Signed visitor tokens
- Account-bound queue positions
- One active position per verified account and event
- Admission leases instead of a destructive pop-before-grant gap
- A globally shared token bucket across replicas
- Queue abandonment handling
- Estimated wait time
- Purchase limits per account, verified phone, card fingerprint, and event
- Bot challenge before joining high-demand queues
- Metrics for admission rate, conversion, abandonment, queue age, and rejected abuse

The existing in-application Redis queue is appropriate for learning and controlled deployments. At large public scale, an edge queue can protect the origin before traffic reaches Next.js or the API. Cloudflare Waiting Room, for example, controls total active users and new users per minute and maintains queue state at the edge.

Sources:

- [Cloudflare Waiting Room architecture](https://developers.cloudflare.com/waiting-room/about/)
- [Cloudflare Waiting Room and bot considerations](https://developers.cloudflare.com/waiting-room/troubleshooting/)

## UX and visual-design direction

### Marketplace homepage

Use a marketplace hierarchy rather than a dashboard hierarchy:

1. Search and location/date controls
2. Featured events
3. Date shortcuts
4. Category chips
5. Popular near the user
6. This weekend
7. Recently added or nearly sold out

Event cards should show:

- Strong event image
- Event title
- Localized date and time
- Venue and city
- Minimum price
- Availability state
- Organizer or category only when useful

Do not overload cards with descriptions, seat counts, and operational metadata.

### Event detail

Above the fold:

- Event image
- Title
- Date/performance selector
- Venue
- Price range
- Availability
- Primary purchase action

Below the fold:

- Description and lineup
- Venue map and transport guidance
- Accessibility
- Age and entry rules
- Refund and cancellation policy
- Organizer information
- Related events

### Checkout

Checkout should be one focused flow:

- Selected tickets or seats
- Hold countdown
- Price and fee breakdown
- Buyer information
- Payment
- Cancellation consequences

The timer must explain what expires. It should not use alarming animation until little time remains.

### Seat map

Show the map and order basket together on desktop. On mobile, use a full-screen map with a persistent selection drawer.

Use SVG first because seats remain semantic, accessible, individually styleable, and easy to inspect. Consider Canvas or WebGL only if real venue sizes prove SVG too slow. Always provide a list-based accessible selection alternative.

Store geometry separately from live availability. A seat-map version is immutable after sales begin; availability changes frequently.

### Organizer interface

Organizer screens should be denser and operational:

- Tables over oversized cards
- Persistent filters
- Saved views where useful
- Bulk actions
- Keyboard navigation
- CSV export
- Clear state and audit columns
- Permission-aware actions

Customer and organizer interfaces should share components and tokens, but not force identical information density.

### Waiting-room interface

Show:

- Event identity
- Queue position
- Estimated wait range
- Current sale status
- Whether refresh is safe
- What happens when admitted
- Session or admission expiry after entry

Fairness rules must be stated truthfully. Do not claim strict FIFO or randomness unless the implementation actually guarantees it.

## Recommended technical tools

### Keep

- ASP.NET Core 10 and C# 14
- EF Core 10 with PostgreSQL
- Redis for distributed ephemeral coordination, caching, SignalR backplane, and waiting-room state
- RabbitMQ plus transactional outbox for asynchronous domain events
- SignalR for live availability and queue updates
- Next.js for marketplace and operations portals
- OpenTelemetry for tracing and metrics
- Testcontainers for infrastructure integration tests
- Kubernetes only as a deployment and multi-replica learning target

### Add when needed

- PostgreSQL full-text search and `pg_trgm` first
- Meilisearch for a product-grade discovery experience after PostgreSQL search limits become visible
- S3-compatible object storage for event media, ticket documents, exports, and seat-map assets
- A job system such as Hangfire or Quartz only when durable scheduled jobs require operator visibility, retries, and manual control
- Edge WAF, bot challenge, and waiting room for a real public flash sale
- A feature-flag system for risky rollout of refund, transfer, resale, and waiting-room policies

### Avoid for now

- Microservices
- Kafka solely because the system has events
- Elasticsearch/OpenSearch before search requirements justify its operational cost
- Event sourcing for normal booking state
- Blockchain tickets
- Native mobile applications before the web/PWA workflow is validated
- A custom payment-card form that expands PCI scope

## Payment integration direction

Keep the provider behind `IPaymentGateway` and implement provider-specific adapters. A production selection should be made only after confirming the organizer's acquiring bank, supported currencies, settlement requirements, refund APIs, webhook model, idempotency guarantees, and local compliance requirements.

Required provider capabilities:

- Stable idempotency for charges and refunds
- Charge and refund status inquiry
- Signed webhooks
- 3-D Secure where required
- Partial and full refunds if the product supports them
- Clear settlement and dispute reporting
- Provider tokenization or hosted payment UI to reduce PCI scope

The platform database remains the booking source of truth, while the payment provider remains the source of truth for whether money moved.

## Recommended next execution sequence

### PR 3 tail: finish safety

1. Add deterministic payment-lease extension race test.
2. Fix the lease save path.
3. Extend `IPaymentGateway` with refund inquiry.
4. Persist the refund initiator.
5. Add outbox backoff, bounded retries, and quarantine.
6. Add broker confirmation/crash tests.
7. Run the full integration suite with Docker.

### Design PR: venue and reserved seating

1. Write an architecture decision record separating `Event`, `Performance`, and `Venue`.
2. Define seat-map versioning and immutability rules.
3. Define seat-level hold and ownership constraints.
4. Define general-admission and reserved-seat coexistence.
5. Define organizer allocation and pricing-zone rules.
6. Review the model before migrations or UI work.

### Implementation PRs

1. Venue/hall/seat-map administration
2. Performance scheduling and pricing zones
3. Reserved-seat availability read model
4. Seat selection and hold flow
5. Organizer allocations, invitations, and promo codes
6. Scanner PWA/offline admission
7. Transfer and face-value return queue
8. Search and discovery enhancements

## Milestone gate

The next product phase should not begin until the following can be demonstrated:

- A payment retry racing the reconciler cannot produce a 500 or corrupt the lease.
- A lost refund response is reconciled from provider truth.
- The original refund actor survives background completion.
- Unroutable and repeatedly failing outbox rows do not retry in a hot infinite loop.
- A confirmed RabbitMQ publish can be safely republished and deduplicated after a process crash.
- The full Docker-backed integration suite is green.

After that gate, the reserved-seating ADR is the appropriate next architecture exercise.
