# Production safety hardening plan

Status: planned  
Created: 2026-07-11  
Applies after: `v3-production`, marketplace M5, virtual waiting room M6, and the flash-sale load test

## Objective

Close the confirmed failure windows that remain after the inventory reservation work:

1. Payment racing hold expiry or another checkout.
2. Crash-unsafe order idempotency.
3. Concurrent refund, ticket scan, and hold release transitions.
4. RabbitMQ publication being marked complete without broker confirmation.
5. Non-atomic and replica-dependent waiting-room admission.

The reservation path already prevents overselling while creating holds. This plan protects the
state transitions that happen after reservation, especially where PostgreSQL, the payment
provider, RabbitMQ, and Redis cannot participate in one atomic transaction.

## Current assessment

The platform is production-shaped, but not production-safe yet.

The following are confirmed defects, not speculative redesign proposals:

- A hold is checked before an external charge and confirmed after it. Hold expiry can release
  the inventory while payment is in flight.
- Two checkouts can charge the same hold under different order IDs.
- An idempotency record can remain `InProgress` forever after a crash between provider success
  and the final database commit.
- Customer and staff refund requests use different provider idempotency keys for the same order.
- Ticket validation and hold release use read-check-write transitions without a concurrency gate.
- The outbox dispatcher does not enable publisher confirms and publishes with `mandatory: false`.
- Waiting-room admission pops visitors before granting their admission keys, and the configured
  admission rate multiplies with the number of API replicas.

The scanned-ticket refund rule is not classified as a defect here. It is an unresolved product
policy and must be decided explicitly before PR 3.

## Non-goals

Do not add product breadth while this plan is active:

- No Elasticsearch.
- No reserved-seating map.
- No microservice extraction.
- No new payment provider beyond what is required to query a payment attempt by idempotency key.
- No architecture framework migration solely for style.

The backend should remain a modular monolith. Separate API and worker hosts may be deployed from
the same codebase later in this plan without splitting business ownership into microservices.

## System invariants

Every implementation and test in this plan should trace back to these invariants.

### Inventory and hold invariants

1. Inventory is never negative and never exceeds total capacity.
2. A hold's quantity is credited back at most once.
3. A hold cannot be both released or expired and successfully purchased.
4. Only one active payment attempt may own a hold at a time.
5. Only one successful order may exist for a hold.
6. A payment with an unknown outcome prevents the hold from returning to general inventory until
   reconciliation proves that no successful charge exists.

### Money invariants

1. One logical checkout uses one stable provider idempotency key for its entire lifetime.
2. Retrying a request cannot create a new provider charge for the same logical checkout.
3. A confirmed provider charge eventually has a corresponding order or an operator-visible
   reconciliation failure.
4. One order can be refunded successfully at most once.
5. The refund provider idempotency key is stable per order, not per caller.

### Ticket invariants

1. One ticket code can transition from `Issued` to `Scanned` at most once.
2. Two scanners racing on the same code produce one success and one conflict.
3. Refund behavior for a scanned ticket follows an explicit product policy.

### Messaging invariants

1. An outbox row is marked processed only after RabbitMQ confirms acceptance.
2. An unroutable message is observable and remains recoverable from the outbox.
3. A transient consumer failure is retried with a bound.
4. An invalid or repeatedly failing message is parked in a dead-letter queue.
5. Consumer side effects remain idempotent under duplicate delivery.

### Waiting-room invariants

1. A visitor cannot be removed from the queue without receiving an admission grant in the same
   Redis operation.
2. The admission rate is global and does not multiply when replicas are added.
3. An admission is short-lived, server-verifiable, event-bound, and limited in use.
4. A client cannot gain unlimited queue positions merely by minting unbounded GUIDs.

## Delivery sequence

The work is divided into five required pull requests and one production-operations pull request.
PR 1 and PR 2 from the original review are intentionally combined into one payment-safety PR,
but the failing tests and implementation remain separate commits.

---

## PR 1: Durable payment state machine and race tests

**Status: DONE** (branch `feature/payment-state-safety`). Delivered as: `test:` red race suite →
`feat:` durable claim + reconciliation → `test:` reconciler completes a crashed checkout unaided →
`docs:` this update. 134 tests green (60 unit + 74 integration). Notes vs the original sketch: the
ambiguous-outcome response is **202 Accepted** (order kept PendingPayment) rather than a bare
error; crash recovery works BOTH on the client's retry (synchronous, via `GetChargeStatus`) and
via the background `PaymentReconciliationService`; multi-replica safety uses the Hold/Order `xmin`
tokens as the compare-and-swap rather than `FOR UPDATE SKIP LOCKED`.

Suggested branch: `feature/payment-state-safety`

Suggested commits:

1. `test: expose checkout expiry and duplicate-payment races`
2. `feat: add durable payment-pending claim and reconciliation`
3. `test: verify crash recovery and concurrent idempotency replay`
4. `docs: record durable payment workflow decisions`

### 1.1 State model

Add a durable payment ownership state to the hold workflow.

Recommended hold transitions:

| From | To | Trigger | Rule |
|---|---|---|---|
| `Active` | `PaymentPending` | Checkout claim | Atomic and only when `ExpiresAt > now` |
| `PaymentPending` | `Confirmed` | Provider confirms success | Same logical order/payment attempt |
| `PaymentPending` | `Active` | Definitive decline | Only if the original hold TTL remains valid |
| `PaymentPending` | `Expired` | Reconciliation proves no charge | Release inventory exactly once |
| `Active` | `Released` | Explicit release | Conditional transition |
| `Active` | `Expired` | Hold-expiry worker | Conditional transition |

`PaymentPending` must not automatically become `Active` when its lease expires. A timeout, lost
response, or process crash cannot prove whether the provider charged successfully. Lease expiry
means "reconciliation work is due", not "payment failed".

Recommended order transitions:

| From | To | Trigger |
|---|---|---|
| new | `PendingPayment` | Hold claim and order creation commit |
| `PendingPayment` | `Confirmed` | Provider success |
| `PendingPayment` | `PaymentFailed` | Definitive decline or reconciled no-charge |
| `Confirmed` | `RefundPending` | PR 2 refund claim |
| `RefundPending` | `Refunded` | Provider refund success |

### 1.2 Persistence changes

Expected schema changes:

- Add `PaymentPending` to `HoldStatus`.
- Add `PaymentLeaseUntil` to `Hold`, or place equivalent lease fields on a dedicated
  `PaymentAttempt` entity.
- Persist the order before the provider call so its ID becomes the stable provider idempotency
  key.
- Add timestamps needed for reconciliation, such as `PaymentAttemptedAt` and
  `PaymentReconciledAt`.
- Add optimistic concurrency protection to `Hold` and `Order`, or use conditional SQL updates
  for every transition. Conditional writes are still required where the transition itself is
  the concurrency decision.
- Add a partial unique PostgreSQL index allowing only one non-failed purchase lineage per hold.
  It initially covers `PendingPayment`, `Confirmed`, and `Refunded`, then includes
  `RefundPending` when PR 2 adds that state. Historical `PaymentFailed` attempts may coexist if
  retry history is retained.
- Extend `IdempotencyRecord` with a lease or recoverable state. It must continue pointing to the
  same stable order ID throughout retries.
- Add database check constraints for state-dependent fields where practical.

Illustrative uniqueness rule:

```sql
CREATE UNIQUE INDEX "UX_Orders_OneActivePurchasePerHold"
ON "Orders" ("HoldId")
WHERE "Status" IN ('PendingPayment', 'Confirmed', 'Refunded');
```

The exact migration must be generated and reviewed through EF Core rather than pasted directly
into a production migration without model configuration.

### 1.3 Checkout flow

The new flow should be:

1. Normalize and validate the API idempotency key.
2. Begin a short PostgreSQL transaction.
3. Claim or load the idempotency record.
4. Atomically transition the hold from `Active` to `PaymentPending`, requiring
   `ExpiresAt > now`.
5. Create the `PendingPayment` order with a stable ID.
6. Persist the idempotency record, order, hold claim, and lease in one transaction.
7. Commit before calling the provider.
8. Call the provider with the persisted order ID as the idempotency key.
9. Finalize in a second short transaction:
   - Success: order and hold become `Confirmed`; write `OrderConfirmed` to the outbox.
   - Definitive decline: order becomes `PaymentFailed`; return the hold to `Active` only if its
     TTL is still valid. Otherwise expire it and release inventory once.
   - Ambiguous failure: keep `PendingPayment`; expose a pending result and schedule
     reconciliation.

No database transaction may remain open while awaiting the payment provider.

### 1.4 Payment reconciliation

Extend `IPaymentGateway` with a status lookup by the stable idempotency key or provider reference.
The dev payment endpoint and WireMock scenarios must support:

- Confirmed charge.
- Definitive decline or not charged.
- Still pending.
- Provider unavailable.

Add a reconciliation worker that:

1. Claims expired payment leases with `FOR UPDATE SKIP LOCKED` or an equivalent compare-and-swap.
2. Queries the provider using the original stable key.
3. Confirms the order when the provider reports success.
4. Fails or expires the attempt only when the provider proves no charge occurred.
5. Extends the lease with backoff when the result remains unknown.
6. Emits metrics and logs for attempts that exceed the operational retry threshold.

The worker must be safe under multiple replicas.

### 1.5 API semantics

Recommended responses:

- `201 Created`: payment is confirmed and the order is complete.
- `409 Conflict`: definitive decline, invalid hold state, expired hold, or mismatched idempotency
  request hash.
- `202 Accepted`: provider outcome is unknown and reconciliation is pending. Include the stable
  order ID and a status endpoint.
- Repeated request with the same key: return the existing order's current state.

Do not encourage a client to create a new idempotency key merely because the previous request
timed out.

### 1.6 Required failing tests before implementation

Use deterministic coordination primitives rather than timing-only tests. WireMock delays alone
are not enough when a barrier or test-controlled provider can make the interleaving exact.

Required integration tests:

- `Checkout_WhenPaymentCrossesHoldExpiry_DoesNotReleaseSoldInventory`
- `ConcurrentCheckout_SameHold_ChargesExactlyOnce`
- `ConcurrentCheckout_SameIdempotencyKey_ReplaysWinnerWithout500`
- `Checkout_CrashAfterProviderSuccess_RecoversOriginalOrder`
- `Checkout_AmbiguousProviderResult_RemainsPendingAndDoesNotReleaseInventory`
- `Reconciliation_ConfirmedCharge_CompletesOriginalOrder`
- `Reconciliation_NoCharge_ExpiresOrReactivatesHoldAccordingToTtl`
- `ExpiryWorker_IgnoresPaymentPendingHold`
- `UniqueActivePurchaseConstraint_BlocksSecondPendingOrderForHold`

Useful test infrastructure:

- A provider barrier that blocks a charge until the test explicitly releases it.
- A test-only EF `SaveChangesInterceptor` that can fail the final confirmation save after the
  provider has returned success.
- A fake or WireMock provider status endpoint for reconciliation.
- A short-TTL factory dedicated to expiry/payment races.

### 1.7 PR 1 completion gate

Do not start the refund/scan work until all of the following are true:

- Two checkouts of one hold result in one provider charge and one successful order.
- A provider success followed by process failure can recover the original order.
- An unknown payment outcome never releases inventory.
- No hold can be confirmed after it was definitively released or expired.
- Concurrent idempotent requests return the same stable order rather than a 500.
- The expiry worker and payment reconciler are safe with multiple replicas.
- Migration review shows the intended unique and concurrency constraints.
- Full backend and frontend verification is green.
- The payment state machine and failure semantics can be explained aloud without reading code.

---

## PR 2: Atomic refund, ticket scan, and hold release

**Status: DONE** (branch `feature/atomic-state-transitions`). Red suite `test: expose concurrent
refund, scan, and release races` → `feat: make refund, ticket scan, and hold release atomic` →
docs → `feat: refund reconciler + per-strategy release tests`. 140 tests green. Scanned-ticket
policy: **non-refundable** (recorded in `docs/ARCHITECTURE.md`). Refund uses a stable
`refund:{orderId}` key + `Confirmed → RefundPending` claim (actor only in the audit event);
ticket scan is an `xmin` compare-and-swap; the optimistic release no longer re-credits on a
hold-row conflict, and pessimistic/redis releases roll back idempotently. The gate is fully met:
the `ConcurrentRelease_*` suite now runs against **all three** strategies, and the
`PaymentReconciliationService` also settles stranded refunds (`RefundClaimedAt` + stale-claim
scan; re-driving uses the stable key so money never moves twice), with a crash-after-refund test.
Next: PR 3 (RabbitMQ publisher confirms + topology + bounded consumer retry).

Suggested branch: `feature/atomic-state-transitions`

Suggested commits:

1. `test: expose concurrent refund scan and release races`
2. `feat: make refund and release transitions atomic`
3. `feat: validate tickets with a conditional state update`
4. `docs: record scanned-ticket refund policy`

### 2.1 Refund design

- Add `RefundPending` to `OrderStatus`.
- Atomically claim `Confirmed -> RefundPending` before calling the provider.
- Use one stable provider key: `refund:{orderId}`. Do not include customer or staff actor IDs.
- Record the initiating actor separately for audit.
- Reconcile ambiguous refunds just like ambiguous charges.
- On provider success, atomically:
  - mark the order `Refunded`;
  - void the ticket according to policy;
  - credit inventory once;
  - write `OrderRefunded` and `AvailabilityChanged` outbox events.

Required tests:

- `ConcurrentRefund_CustomerAndStaff_ProviderRefundsExactlyOnce`
- `Refund_CrashAfterProviderSuccess_ReconcilesOriginalOrder`
- `Refund_RetryUsesStableOrderKey`
- `Refund_InventoryIsCreditedExactlyOnce`

### 2.2 Scanned-ticket refund decision

Choose one policy and record it in `docs/ARCHITECTURE.md`:

1. Reject refunds after scan.
2. Allow refunds after scan and record a post-admission refund audit event.
3. Allow only an authorized staff override.

The current behavior remains allowed until this decision is made. The implementation must then
enforce and test the chosen rule.

### 2.3 Ticket scanning

Use an atomic transition equivalent to:

```sql
UPDATE "Tickets"
SET "Status" = 'Scanned', "ScannedAt" = @now
WHERE "Code" = @code AND "Status" = 'Issued'
RETURNING ...;
```

Required tests:

- `ConcurrentScan_SameCode_OneSuccessOneConflict`
- `Scan_VoidedTicket_ReturnsConflict`
- `Scan_UnknownCode_ReturnsNotFound`

### 2.4 Hold release

The status transition and inventory credit must be one atomic operation. A caller must not mark
the hold released in memory and then independently retry the inventory credit.

Required tests for every reservation strategy:

- `ConcurrentRelease_SameHold_CreditsInventoryOnce`
- `Release_RacingExpiry_CreditsInventoryOnce`
- `Release_RacingReservation_PreservesInventoryInvariant`

### 2.5 PR 2 completion gate

- Refund is one logical money movement regardless of caller or retry.
- Ticket scan admits once under concurrent scanners.
- Hold quantity is credited at most once under release/expiry races.
- The scanned-ticket refund policy is explicit and tested.
- All three reservation strategies pass their release-race suite.

---

## PR 3: RabbitMQ publication and consumer reliability

**Status: DONE** on branch `feature/rabbitmq-delivery-safety`: a `RabbitMqTopologyInitializer` declares the exchange, DLX,
and every consumer queue + binding as a plain `IHostedService` whose `StartAsync` completes
BEFORE the dispatcher starts (§3.1), and the dispatcher now publishes with **publisher confirms +
tracking** and `mandatory: true`, marking a row processed only after the broker ACKs and leaving a
returned/unconfirmed message in the outbox for retry (§3.2). Shared `RabbitMqTopology` is the one
source of truth; this also fixed a latent bug — `OrderRefunded` had **no binding** and was being
silently dropped, so it is now routed to `notifications` (which handles both confirm and refund).

The dispatcher also has persistent exponential retry scheduling (`NextAttemptAt`), a configurable
attempt budget, and operator-visible quarantine (`FailedAt` + `LastError`) instead of hot-looping
an unroutable row. Consumers share one failure policy: malformed payloads dead-letter immediately;
transient failures publish with confirms to durable per-consumer/per-event TTL retry queues, carry
an attempt header, and park after the configured total-attempt bound. Per-event retry queues avoid
fan-out contamination when two consumers subscribe to `OrderConfirmed`. The availability
projection records dedupe only after its SignalR side effect, so a transient broadcast failure is
actually retried (a duplicate absolute-state push after a crash is safe). Tests cover outbox
backoff/quarantine, transient recovery, bounded exhaustion, and poison payloads.

Integration events now cross the Application boundary as typed `IIntegrationEvent` records rather
than `(string, anonymous object)`. The outbox persists schema version, tenant, correlation, and
trace metadata. The dispatcher wraps the typed payload in a stable envelope containing
`messageId`, `eventType`, `schemaVersion`, `occurredAt`, `tenantId`, `correlationId`, and `payload`.
Consumers verify that envelope identity matches AMQP `MessageId` and the routing key before they
deserialize a typed payload or perform a side effect. Unsupported versions and invalid contracts
are poison; old pending outbox rows remain publishable because tenant metadata can be recovered
from the legacy payload.

The gate is now fully met (§3.5, §3.6): `Topology_IsReadyBeforeFirstPublish` passive-declares
every exchange, main queue, retry queue, and the DLQ (the initializer ran before dispatch);
`TicketIssuer_DuplicateConcurrentDelivery_ProducesMatchingFileAndDatabaseCode` proves duplicate
`OrderConfirmed` delivery leaves exactly one ticket row and one matching PDF credential; and the
messaging path is observable — outbox backlog age (gauge), returned/retried/quarantined counters,
confirmation-latency histogram, and consumer retry/dead-letter counters, all on the exported
`TicketingPlatform` meter. **153 backend tests green (60 unit + 93 integration)** against real
PostgreSQL, Redis, and RabbitMQ; Release build 0 warnings. Next: PR 4 (waiting-room atomicity and
global admission control).

The dispatcher’s broker transport is isolated behind `IOutboxPublisher`. A deterministic test
injects one pre-confirm transport loss, proves the claimed row remains unprocessed with its same
message ID, and then proves the real publisher delivers it after the configurable claim lease
expires. `OutboxLockSeconds` remains 30 seconds in production and is shortened only by the test
factory. The broker-interruption failure window is therefore covered without timing-dependent
container restarts.

The complementary post-confirm crash test proves at-least-once delivery rather than exactly-once:
RabbitMQ receives the first copy, a simulated process loss prevents `ProcessedAt` from being saved,
and the same outbox row publishes a second copy after its lease expires. Both broker copies carry
the same `MessageId`; downstream per-consumer dedupe is the mechanism that makes this safe.

**Still open (PR 3 tail):** the remaining §3.5 tests
(the duplicate-delivery ticket-issuer test and an explicit topology-ready test).

Suggested branch: `feature/rabbitmq-delivery-safety`

Suggested commits:

1. `test: cover unroutable and interrupted outbox publication`
2. `feat: initialize rabbitmq topology before dispatch`
3. `feat: require publisher confirms and mandatory routing`
4. `feat: add bounded transient consumer retries`
5. `refactor: introduce versioned integration event envelopes`

### 3.1 Topology initialization

- Declare the exchange, durable queues, bindings, retry queues, and DLQ before the dispatcher can
  publish.
- Do not rely on consumers racing to create their queues during process startup.
- Make startup ordering explicit through a topology initializer or a shared readiness task.
- Fail readiness or pause dispatch if required topology cannot be verified.

### 3.2 Confirmed publication

- Create the publisher channel with publisher confirms enabled.
- Publish persistent messages with `mandatory: true`.
- Handle returned unroutable messages.
- Mark an outbox row processed only after RabbitMQ confirms the publish.
- On negative confirmation, return, connection loss, or timeout, leave the row unprocessed and
  release or expire its claim for retry.
- Dispose and recreate broken channels and connections rather than dropping references only.

### 3.3 Consumer retry policy

Differentiate:

- Transient failures: database outage, temporary storage failure, network interruption.
- Poison failures: invalid JSON, unsupported event version, missing required fields.
- Exhausted failures: transient failure that exceeded its bounded retry policy.

Use bounded delayed retries, then dead-letter. Do not dead-letter every exception on the first
attempt, and do not requeue indefinitely.

### 3.4 Event contracts

Replace anonymous payload parsing with a versioned envelope containing at least:

- `MessageId`
- `EventType`
- `SchemaVersion`
- `OccurredAt`
- `TenantId` where applicable
- typed payload
- trace context

Add compatibility tests for every consumer.

### 3.5 Required tests

- `Outbox_UnroutableMessage_RemainsUnprocessed`
- `Outbox_BrokerDisconnectBeforeConfirm_RetriesSameMessage`
- `Outbox_PublishBeforeProcessedCrash_DeliversAtLeastOnce`
- `Topology_IsReadyBeforeFirstPublish`
- `Consumer_TransientFailure_RetriesThenSucceeds`
- `Consumer_InvalidPayload_DeadLettersWithoutInfiniteLoop`
- `TicketIssuer_DuplicateConcurrentDelivery_ProducesMatchingFileAndDatabaseCode`

### 3.6 PR 3 completion gate

- No outbox row is completed without positive broker ownership confirmation.
- Missing bindings are detected rather than silently dropping messages.
- Transient failures retry and poison messages park.
- Duplicate delivery cannot corrupt ticket PDF/database consistency.
- Outbox backlog, retry, return, negative-confirmation, and DLQ metrics exist.

---

## PR 4: Waiting-room atomicity and global admission control

**Status: DONE** on branch `feature/waiting-room-safety`. `RedisWaitingRoom.AdmitBatchAsync` is now one Lua script: it pops
from the line AND writes each visitor's TTL'd admission grant in the same operation (no
pop-before-grant crash window), reads the next positions, and de-registers the event from the
active set only when the line is empty at script time (a concurrent join can't be orphaned by a
racing cleanup). Admissions are metered by a per-event Redis **token bucket** refilled from
Redis's own clock at `WaitingRoom:AdmitRatePerSecond` (burst `AdmitBurst`), so the effective rate
is constant regardless of how many API/worker replicas run an admitter — `AdmitBatchSize` is now
only a per-call efficiency cap. Tests (real Redis): `Admission_ScriptCannotPopWithoutGrant`
(conservation), `ConcurrentAdmitters_RespectOneGlobalRate`, `JoinRacingQueueCleanup_RemainsDiscoverable`.

§4.3 is also done: an admission is now a Redis **hash grant** (quota + bound customer, TTL'd)
written only by the admitter. Hold authorization calls `TryConsumeAdmissionAsync` — one atomic Lua
op that verifies the grant for THIS event, binds it to the authenticated customer on first use (a
leaked visitor id is useless to another account → 403), and decrements the per-admission hold
quota (exhausted → 429). Anonymous joins are throttled per client (IP) by a Redis fixed-window
counter so nobody mints unlimited positions (→ 429). Tests: expired-grant-cannot-hold,
grant-cannot-be-used-for-another-event, grant-cannot-be-reused-beyond-quota, grant-binds-to-first-
customer, multiple-visitor-ids-are-rate-limited. **PR 4 gate (§4.5) fully met** — admission atomic
in Redis, rate independent of replica count, a leaked GUID alone can't reserve, reuse/position
abuse bounded, polling + SignalR still fallback-compatible. 161 tests green (60 unit + 101
integration); Release build 0 warnings.

Suggested branch: `feature/waiting-room-safety`

Suggested commits:

1. `test: expose admission crash and replica-rate defects`
2. `feat: make redis admission atomic with lua`
3. `feat: add a global distributed admission token bucket`
4. `feat: issue limited server-verifiable admission grants`
5. `test: cover queue abuse and admission consumption`

### 4.1 Atomic Redis operation

Use one Lua script to:

1. Pop up to the globally permitted number of visitors.
2. Create their admission grants with TTL.
3. Read the next visible queue positions.
4. Remove the event from the active registry only if the queue is still empty at script time.

There must be no crash window between queue removal and admission creation.

### 4.2 Global rate

- Store token-bucket state in Redis.
- Use Redis server time inside the script where possible.
- Make the configured rate independent of the number of API or worker replicas.
- Separate batch size from admission rate. A larger batch may reduce overhead but must not alter
  the long-term rate.

### 4.3 Admission identity and consumption

- Replace trust in a bare client-generated GUID with a server-verifiable grant.
- Bind the grant to the event and server-issued visitor session.
- Bind it to the authenticated customer on first hold use when checkout requires authentication.
- Limit how many holds or tickets one admission may reserve.
- Consume or decrement the grant atomically with hold authorization.
- Add join throttling and decide whether high-risk events require CAPTCHA or authenticated queue
  entry.

### 4.4 Required tests

- `Admission_ScriptCannotPopWithoutGrant`
- `ConcurrentAdmitters_RespectOneGlobalRate`
- `JoinRacingQueueCleanup_RemainsDiscoverable`
- `Admission_ExpiredGrantCannotCreateHold`
- `Admission_GrantCannotBeUsedForAnotherEvent`
- `Admission_GrantCannotBeReusedBeyondQuota`
- `Admission_MultipleVisitorIdsAreRateLimitedBySessionOrClientPolicy`

### 4.5 PR 4 completion gate

- Admission is atomic in Redis.
- Replica count does not change the configured admission rate.
- A leaked visitor GUID alone is insufficient to reserve.
- Admission reuse and queue-position abuse have explicit bounds.
- Polling and SignalR remain fallback-compatible.

---

## PR 5: Authentication session concurrency and proxy-aware rate limiting

Suggested branch: `feature/session-safety`

### Scope

- Make refresh-token rotation atomic.
- Prevent concurrent BFF requests from treating legitimate parallel refreshes as token theft.
- Add server-side refresh-token revocation on logout.
- Add a BFF single-flight refresh mechanism or a server-side rotation grace/session-version
  design suitable for multiple web replicas.
- Configure trusted forwarded headers before IP-based rate limiting.
- Decide whether login limits must be distributed across API replicas.
- Validate security-sensitive options at startup.

Required tests:

- `ConcurrentRefresh_SameSession_DoesNotForkOrRevokeLegitimateSession`
- `Logout_RevokesRefreshTokenServerSide`
- `Refresh_ReplayOutsideGrace_RevokesSessionFamily`
- `RateLimiter_UsesTrustedForwardedClientIp`
- `RateLimiter_DoesNotTrustUnconfiguredProxyHeaders`

---

## PR 6: Deployment, storage, health, and operational hardening

Suggested branch: `feature/production-operations`

### 6.1 Separate API and workers

Allow the same repository and business modules to run as separate host profiles:

- API host: controllers, auth, SignalR, health, no polling workers by default.
- Worker host: outbox, consumers, expiry, payment reconciliation, waiting-room admission.

This keeps the modular monolith while allowing independent replica counts. Scaling HTTP traffic
must not multiply admission valves or scheduled work.

### 6.2 Readiness semantics

Classify dependencies:

- PostgreSQL: hard API dependency.
- RabbitMQ: asynchronous processing dependency; an outage should normally buffer the outbox
  rather than remove every API pod from service.
- Redis: conditional hard dependency for waiting-room-gated events or Redis reservation mode;
  otherwise cache and realtime features may degrade.
- Payment provider: checkout dependency, not necessarily whole-API readiness.

Expose detailed dependency health separately from the traffic readiness decision.

### 6.3 Object storage

- Replace the multi-replica `ReadWriteOnce` ticket volume with MinIO/S3 or a genuinely shared
  RWX storage implementation.
- Make file writes idempotent and atomic where the provider supports it.
- Clean up orphaned or superseded images and documents.
- Preserve the existing `IFileStorage` port.

### 6.4 Observability and retention

Add metrics and alerts for:

- Payment attempts by outcome.
- Payment reconciliation age and backlog.
- Refund reconciliation age and backlog.
- Hold expiry lag.
- Outbox backlog and oldest unprocessed age.
- Publisher returns, negative confirmations, and retries.
- Dead-letter queue depth.
- Waiting-room depth and actual global admission rate.
- Ticket scan conflicts.

Add retention jobs for processed outbox rows, processed-message dedupe rows, expired refresh
tokens, completed idempotency records, and old waiting-room state.

### 6.5 CI and documentation

- Run Playwright in CI against an actual API and web stack.
- Build both API and web Dockerfiles in CI.
- Keep NuGet and npm vulnerability audits in CI according to an explicit failure policy.
- Add container-image scanning and dependency update automation.
- Resolve the EF `has-pending-model-changes` false positive before making it a blocking CI gate.
- Update `README.md`, `AGENTS.md`, and `docs/ARCHITECTURE.md` after each completed milestone.

---

## Verification matrix

Every required PR must run the checks relevant to its risk.

### Backend baseline

```powershell
dotnet build TicketingPlatform.sln -c Release
dotnet test tests/TicketingPlatform.UnitTests/TicketingPlatform.UnitTests.csproj -c Release
dotnet test tests/TicketingPlatform.IntegrationTests/TicketingPlatform.IntegrationTests.csproj -c Release
dotnet ef migrations has-pending-model-changes `
  --project src/TicketingPlatform.Infrastructure `
  --startup-project src/TicketingPlatform.Api
```

### Frontend baseline

```powershell
Set-Location apps/web
npm.cmd run typecheck
npm.cmd run lint
npm.cmd run build
npm.cmd run e2e
```

### Concurrency verification

- Race tests must coordinate exact interleavings with barriers or controllable dependencies.
- Do not depend only on `Task.Delay` to reproduce correctness defects.
- Repeat concurrency-sensitive tests enough to expose accidental nondeterminism while keeping one
  deterministic proof of the intended interleaving.
- Run release-credit tests against all three `IReservationStrategy` implementations.
- Re-run the flash-sale load test after changing reservation or release behavior.

### Manual demonstration

For each milestone, demonstrate:

1. The previous failure window using the test or an instrumented flow.
2. The state transition or database constraint that closes it.
3. Recovery after process, database, broker, or provider interruption.
4. The trade-off and the case where the chosen pattern would be excessive.

## Interview-readiness questions produced by this work

- Why can a concurrency-safe inventory decrement still produce overselling later?
- Why must a database transaction not stay open across a payment-provider call?
- What is the difference between a payment decline and an ambiguous payment result?
- Why is an idempotency key insufficient without durable operation state?
- How do leases and reconciliation work together?
- Why is a partial unique index useful for payment attempts on one hold?
- What does RabbitMQ publisher confirmation prove that `Persistent = true` does not?
- Why does `mandatory: true` matter for an outbox publisher?
- When should a consumer retry versus dead-letter?
- Why can a Redis operation be individually atomic while the overall workflow is not?
- Why does a per-process rate limiter or admission loop change behavior when replicas scale?
- When is a modular monolith with separate worker deployments preferable to microservices?

## Immediate next actions

Finish the PR 3 failure-window evidence, then move to PR 4.

1. Add a controllable broker interruption immediately before publisher confirmation and prove the
   same outbox message is retried without being marked processed early.
2. Add the publish-confirmed/process-crash-before-`ProcessedAt` duplicate-delivery test.
3. Deliver the same `OrderConfirmed` concurrently to the ticket issuer and prove one matching
   database ticket/file credential is produced.
4. Add an explicit startup test proving all main, retry, and dead-letter queues exist before the
   first dispatch.
5. Add backlog, retry, return, confirmation, and DLQ metrics required by the PR 3 completion gate.
6. Close PR 3 only after the full Docker-backed suite and migration check are green.
7. Start PR 4 with deterministic waiting-room pop/grant crash and multi-replica admission tests.
