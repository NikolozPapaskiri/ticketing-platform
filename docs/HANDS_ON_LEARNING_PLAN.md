# Own the system: hands-on .NET and DevOps learning plan

Created: 2026-09-10. Reference checkout: `588f60d`.

**Owner:** you. **Coach and reviewer:** AI. **Current learning status:** not yet demonstrated.

The existing application is your reference system and a source of realistic exercises. Your objective is to independently build, explain, debug, test, change, and operate it. A feature being present in the repository does not count as you having learned it.

Your OutSystems experience gives you domain and delivery context. We will still start .NET mechanics from the basics: files, projects, the compiler, methods, objects, the debugger, HTTP, SQL, and the shell. Familiar concepts can move faster only after you demonstrate them yourself. This plan develops portable engineering skills and evidence of your work; it makes no promise about a particular technology or employment outcome.

**Read today:** the working agreement, the simple request explanation, and Session 1. The rest is a reference curriculum to open one stage at a time, not a reading assignment to finish before touching code.

## 1. The working agreement

This records your explicit instruction of 2026-09-10. For these learning sessions it replaces the older assumptions in `AGENTS.md` and `CLAUDE.md` that AI should implement changes, fix builds, or execute the development work for you. The product roadmap still describes product priorities. **This file describes your learning priorities.**

### You own every implementation step

- You write application code, tests, SQL, configuration, scripts, Dockerfiles, pipelines, manifests, and learning notes.
- You use the terminal, debugger, browser developer tools, database tools, Git, and deployment tools yourself.
- You reproduce errors, inspect evidence, propose fixes, make corrections, and rerun checks.
- You author your commits, PR descriptions, diagrams, decisions, and incident reports.
- Standard compiler, IDE, `dotnet`, and EF scaffolding is allowed. You invoke it, understand what it generated, and review its diff. Generating a migration is not a substitute for reading its SQL.
- Disable generative code completion while doing assessed exercises. Ordinary autocomplete, navigation, documentation, and compiler diagnostics are useful tools to learn.

### AI is the coach

- Explain why a concept exists, what executes under the hood, common mistakes, the tradeoff, and when the approach is unnecessary or wrong.
- Give one small assignment at a time, with a behavior specification and observable acceptance criteria.
- Explain unfamiliar terminology before relying on it. Use an OutSystems analogy if helpful, then explain where that analogy stops matching .NET.
- Inspect existing source and your attempted changes, ask questions, suggest experiments, and review the evidence you provide.
- Give hints in order: a question, a concept, a documentation/API pointer, then focused feedback on your attempt.
- Do not write exercise solutions, method bodies, test implementations, migrations, deployment YAML, or replacement patches. Do not execute your build, test, migration, release, or recovery exercises for you.
- Do not turn a request such as "help me fix this" into an automatic code edit. Guide you through diagnosing and correcting it.
- A small unrelated conceptual example is possible if you explicitly ask for one. It must not solve the current assignment in disguise.
- If you are stuck, reduce the problem and explain the missing prerequisite. Do not leave you guessing indefinitely or complete it on your behalf.

Creating this planning document is the explicit exception for this session. No application implementation is part of that exception. Any later change to the coaching agreement must be explicit, not silently inferred from frustration or a deadline.

### Use the existing application without copying it

Use two modes:

1. **Trace mode:** inspect a working path to understand its behavior and dependencies.
2. **Rebuild mode:** close the reference implementation, work from a behavior specification, write your version, and compare only after you have a working attempt.

Keep experiments in a learner-created `learning/` directory or a separate short-lived `feature/learn-*` branch. Keep scratch projects outside the main solution initially. Reuse one small ticketing lab across lessons instead of starting many abandoned projects. Keep intentionally broken concurrency, security, and deployment experiments on disposable local data.

For actual product improvements, use a separate `feature/*` branch and preserve the existing regression suite. Alternative implementations are learning comparisons; only one justified version needs to remain in the application. Do not erase the current system or replace it wholesale to prove ownership.

## 2. What this project actually does

An organizer is a tenant. It publishes events and sells a limited number of tickets. A customer can buy from different organizers using one account. Platform administrators manage the service across organizers.

There are three connected problems:

1. **Selling:** find an event, reserve inventory temporarily, pay, and receive a ticket.
2. **Correctness:** two buyers must not acquire the same capacity; retries and crashes must not create duplicate business effects.
3. **Operating:** expire unused holds, recover uncertain payments, deliver background work, control demand, admit customers, and diagnose failures.

### The moving parts

| Part | What it does | Start reading when needed |
|---|---|---|
| Browser and Next.js | Displays the marketplace and portals. Browser mutations commonly go through the BFF, the server layer dedicated to the frontend. Some public reads run on the Next.js server. | [client-api.ts](../apps/web/lib/client-api.ts), [public-api.ts](../apps/web/lib/server/public-api.ts) |
| BFF and session handling | Reads HttpOnly cookies server-side, forwards authenticated API calls, and handles refresh/logout. | [auth.ts](../apps/web/lib/server/auth.ts), [api.ts](../apps/web/lib/server/api.ts) |
| API | Receives HTTP, applies middleware/auth/validation, selects a controller, and returns a response. | [Program.cs](../src/TicketingPlatform.Api/Program.cs), [PublicEventsController.cs](../src/TicketingPlatform.Api/Controllers/PublicEventsController.cs) |
| Application | Coordinates a use case through services and interfaces. | [EventService.cs](../src/TicketingPlatform.Application/Services/EventService.cs), [HoldService.cs](../src/TicketingPlatform.Application/Services/HoldService.cs), [OrderService.cs](../src/TicketingPlatform.Application/Services/OrderService.cs) |
| Domain | Represents business data and guarded state transitions. | [Event.cs](../src/TicketingPlatform.Domain/Event.cs), [Inventory.cs](../src/TicketingPlatform.Domain/Inventory.cs), [Hold.cs](../src/TicketingPlatform.Domain/Hold.cs), [Order.cs](../src/TicketingPlatform.Domain/Order.cs) |
| Persistence | EF maps objects and queries to PostgreSQL; migrations evolve the schema. | [TicketingDbContext.cs](../src/TicketingPlatform.Infrastructure/Persistence/TicketingDbContext.cs), [EventRepository.cs](../src/TicketingPlatform.Infrastructure/Persistence/Repositories/EventRepository.cs) |
| Access scopes | Tenant, customer, public, platform, and system reads have different access rules. A customer's orders span organizers but remain restricted by ownership. | [AccessScopes.cs](../src/TicketingPlatform.Infrastructure/Persistence/Scopes/AccessScopes.cs) |
| Redis | Cache, waiting-room coordination, SignalR backplane, and one reservation strategy. These are different responsibilities even though they share a server. | [RedisCacheService.cs](../src/TicketingPlatform.Infrastructure/Caching/RedisCacheService.cs), [RedisWaitingRoom.cs](../src/TicketingPlatform.Infrastructure/WaitingRoom/RedisWaitingRoom.cs) |
| Outbox, RabbitMQ, workers | Persist intended events with business changes, publish them, and process work such as ticket issuing and availability updates. | [OutboxDispatcher.cs](../src/TicketingPlatform.Infrastructure/Messaging/OutboxDispatcher.cs), [TicketIssuerConsumer.cs](../src/TicketingPlatform.Infrastructure/Messaging/TicketIssuerConsumer.cs) |
| File storage | Stores ticket PDFs separately from their database metadata. | [IFileStorage.cs](../src/TicketingPlatform.Application/Abstractions/IFileStorage.cs), [S3FileStorage.cs](../src/TicketingPlatform.Infrastructure/Storage/S3FileStorage.cs) |
| Delivery and operations | Builds, tests, packages, runs, observes, and eventually recovers the application. | [Compose](../docker-compose.yml), [CI](../.github/workflows/ci.yml), [Kubernetes](../k8s/kustomization.yaml), [monitoring](../docker-compose.observability.yml) |

An interface is a contract. Dependency injection chooses and constructs the implementation. It does not turn a method call into a network call. The four backend projects normally execute within the same application process. The API and worker can also run as separate processes from the same backend image. Follow the actual runtime calls separately from project-reference arrows.

### Follow the system in this order

**First, a simple read:**

`GET /api/v1/public/tenants/{tenantSlug}/events`

`PublicEventsController.List -> EventService.ListPublicAsync -> IEventRepository / EventRepository -> PublicScope -> EF -> PostgreSQL -> result/DTO -> JSON`

There is a tenant lookup and separate count/list work. Locate each database execution yourself. This route lets you study a request without first understanding checkout, authentication, or RabbitMQ.

**Then, a write:** an organizer creates or updates an event. Follow binding, validation, role/tenant checks, the application service, entity changes, `SaveChangesAsync`, and the returned response.

**Then, a purchase:** the customer hold endpoint resolves the selling tenant, verifies sale/queue rules, and reserves inventory. Checkout persists a payment claim and order before calling the provider. A definitive result settles the state; an uncertain outcome remains pending for inquiry/reconciliation. Outbox publication leads to ticket issuing and availability updates. Download and admission have their own authorization/state checks. See [OrderService.cs](../src/TicketingPlatform.Application/Services/OrderService.cs) after the simpler flows make sense.

### Current facts that prevent learning the wrong model

- Dates belong to [Performance](../src/TicketingPlatform.Domain/Performance.cs). `Event.HeadlineDate` summarizes scheduled dates. A response named `StartsAt` does not mean `Event.StartsAt` still exists.
- Venue geometry and performance foundations exist. Full reserved-seat purchasing does not. The current sale flow uses quantity-based inventory.
- The configured payment endpoint is a development stub. This is suitable for learning recovery, not evidence of a real payment-provider launch.
- Base Compose uses a combined API/worker host. Kubernetes has separate API and worker deployments.
- The existing availability projection does not mean every browse/detail query avoids transactional inventory reads.
- The Redis reservation strategy still accesses PostgreSQL and has a cross-store recovery tradeoff. Do not learn "Redis removes the database" from older prose.
- The historical load harness measures holds. A successful hold is not a completed purchase.
- Older document status blocks and green-test totals are historical. Run your own checks and record your results.

## 3. The practice loop and proof of learning

Every assignment follows this loop:

1. **Predict:** write the expected behavior, data changes, errors, and any uncertainty.
2. **Implement:** make the smallest attempt yourself.
3. **Run:** compile, exercise it, and run your tests yourself.
4. **Inspect:** use output, debugger, SQL, logs, or metrics to explain the result.
5. **Diagnose:** change one condition or investigate a real failure.
6. **Explain:** describe what executed and why your implementation works.
7. **Repeat later:** perform a small variation with the previous solution closed.

Before coding, write acceptance cases in plain English. You author the tests. A good test catches a specific plausible defect; a test that repeats the implementation's logic is weak evidence. Existing tests remain regression protection but do not substitute for tests you can design and interpret.

Use this evidence template in your own learner-created notes:

| Field | You fill in |
|---|---|
| Exercise, date, branch, commit | Exact work being assessed |
| Requirement and prediction | Expected behavior before running |
| Changes and commands | What you wrote and executed; omit secrets |
| Observed evidence | Test output, HTTP response, SQL, trace, or measured result |
| Failure and diagnosis | Symptom, hypothesis, experiment, cause, correction |
| Explanation | Request/transaction/data flow in your own words |
| Alternative and tradeoff | What changed and when you would choose it |
| Cold repeat | Date and outcome without the solution open |

A stage passes only when you can **implement, explain, diagnose, vary, and repeat** its core behavior. Mark "observed" separately from "implemented independently." No stage is passed because AI wrote it earlier or because you recognize the code when reading it.

If you need help, bring: "I expected X, observed Y, checked Z, and my current hypothesis is H." If you cannot form H yet, say which part of the execution model is unclear. That is a teaching opportunity, not a failure.

## 4. Start here: the first three sessions

These are bounded starting sessions, not a demand to complete the whole corresponding stage in one sitting. Aim for 60-90 focused minutes each; split further when an unfamiliar tool needs its own lesson.

### Session 1: own one running request

**Your work:**

1. Inspect Git status and record the starting commit. Create your own learning branch after understanding any existing changes.
2. Identify the SDK, Docker availability, solution, projects, and launch profile. Explain build versus run and SDK versus runtime. Write the inspection commands yourself using tool help or the runbook.
3. Choose the host-debugging route: run dependencies in containers and the API on the host. Read [Compose](../docker-compose.yml), [launchSettings.json](../src/TicketingPlatform.Api/Properties/launchSettings.json), and the run instructions in [README](../README.md). Ensure a container API is not already occupying host port 5000.
4. Build and start the API yourself. Use the public tenant directory or seeded data to find a real slug. Request the tenant event list. Locate the corresponding controller and log output.
5. Predict and try an invalid page number, an unknown tenant, and a valid request. Record the actual status/body for each. Do not assume all existing endpoints have perfectly uniform errors.
6. Draw browser/client, API, and database as boxes, including the ports. Add Redis, RabbitMQ, storage, and workers as boxes you will investigate later.

**Return to the coach with:** your commands/output, one successful response, one failure response, the diagram you drew, and three specific unknowns.

**Stop point:** a request that you personally ran and can locate in source. Do not start by reading all of `OrderService`.

If Docker or the API will not start, diagnose the actual error with the coach. Record the environment gate as pending. You may still do Session 2's independent console work while resolving infrastructure.

### Session 2: write a small ticketing program yourself

Create a small console exercise in your learning area, using normal .NET tooling yourself. No database, web framework, broker, or borrowed repository implementation yet.

**Behavior:** start with 10 available tickets; reserve 3, then 4; reject a request for 5 without changing availability; reject zero/negative quantities; calculate a total price; print an event summary. Design the methods and data types yourself. Do not model refunds yet.

Run it, step through it in the debugger, then write your first tests. Explain every variable, condition, method call, and returned value. Try a loop and then LINQ for the same small reporting calculation. Test that both produce the same result.

**Stop point:** you can change the capacity or a rule without asking AI to rewrite the program. Classes, records, interfaces, time, and async build on this in B1; they do not all need to fit into this session.

### Session 3: trace and reconstruct the read

Set breakpoints in the public events controller, application service, and repository. Execute the Session 1 request again. Inspect arguments, injected dependencies, the call stack, the SQL execution point, and the response mapping.

Then close the reference and sketch a tiny in-memory HTTP endpoint with equivalent list/get behavior in your lab. Write it yourself after the coach explains the missing HTTP/DI concepts. This may take more than one session.

**Return with:** your own request trace, an explanation of where SQL executes, and your first endpoint attempt. A successful orientation trace is not yet mastery of EF or authentication.

## 5. Backend learning stages

Work through B0-B12 in order. Testing and debugging are present throughout. Run the DevOps stages alongside them at the checkpoints shown later. A stage is a collection of small assignments, not one giant ticket.

### B0. Tools, runtime, and system map

**Purpose:** remove the mystery between source files and a running process.

**You do:** complete Session 1; identify `.sln`, `.csproj`, packages, build outputs, startup project, configuration sources, and logs. Run a single unit test and then the unit project. Stop/start your API and explain what state survives. Learn breakpoints, step over/into/out, watches, exceptions, and call stacks.

**Investigate:** a wrong port, a missing setting, or a build error in your lab. Distinguish a compiler error, startup error, HTTP error, and dependency error.

**Gate:** reproduce your startup and one request from your own short runbook, explain the process map, and locate a failure without AI executing commands. Pair with D0-D1.

### B1. C# from small, executable behavior

**Purpose:** gain direct control of the language before navigating framework abstractions. Use [Microsoft's C# reference and learning entry point](https://learn.microsoft.com/en-us/dotnet/csharp/tour-of-csharp/) for the concept currently being practiced.

**You implement, in small steps:**

1. Methods, branching, loops, numeric types, strings, and parsing through Session 2's inventory exercise.
2. Classes, constructors, properties, access modifiers, and enums; keep invalid state out of your objects.
3. A read DTO using a record; compare mutation, assignment, equality, and copying with your entity class.
4. Nullable fields, explicit missing values, and boundary checks. Avoid using `!` simply to silence a warning.
5. Lists, dictionaries, LINQ filtering/projection/grouping, and deferred enumeration. Change an input collection before enumeration and predict the result.
6. An interface with two small implementations, a useful generic collection/helper, and the difference between interface dispatch and inheritance.
7. Price calculations with `decimal`, explicit rounding decisions, timestamps, an expiry boundary, exceptions, and `using`/disposal for a small file exercise.
8. A minimal async exercise: completion, exception, and cancellation. Save concurrency tuning for B6.

**Variations:** loop versus LINQ; mutable class versus read record; system time versus an injected clock. Compare behavior and readability, not just line count.

**Gate:** write a small new rule from requirements, test boundary cases, explain reference/value behavior and deferred execution, and debug it without the original open. Later, repeat the exercise with a slightly different rule.

**Interview prompts:** what does assignment copy; when does LINQ execute; what does an interface buy; when is a record a poor entity choice; why does deterministic time help testing?

### B2. Build HTTP and understand dependency injection

**Purpose:** see how a request reaches your C# code and how its dependencies are constructed. Reference: [ASP.NET Core fundamentals](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/?view=aspnetcore-10.0).

**You implement:** a small in-memory event API in your lab with create/list/get, JSON contracts, status codes, and input validation. Start with a controller. Separate storage behind an interface only after the direct version works. Add one logging middleware and one typed configuration setting. Trace middleware order and request cancellation.

**Experiments:** predict scoped/transient/singleton reuse by logging instance identities. Reproduce a scoped dependency captured by a singleton in the lab with scope validation enabled. Diagnose why the service graph is wrong. Do not turn a real `DbContext` into a singleton.

**Variation:** implement one equivalent read as a Minimal API, using the same acceptance cases. Keep one approach after explaining routing, binding, testing, and organization costs.

**Gate:** explain routing -> binding -> validation -> handler -> serialization, predict 400/404/201 outcomes, and construct a small DI graph yourself. Explain when an extra service/interface is unnecessary.

### B3. SQL, EF, and persistence ownership

**Purpose:** connect object changes to database behavior. References: [EF tracking](https://learn.microsoft.com/en-us/ef/core/querying/tracking), [EF concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).

**You implement:** replace the lab's in-memory store with PostgreSQL and EF. Design an event/ticket-type relationship, keys, constraints, and an index. Author the mapping, generate a migration yourself, read the SQL, apply it to a disposable database, and inspect the resulting schema. Persist, reload, update, and restart.

**Experiments:** inspect tracked entity states around `SaveChanges`; compare an entity graph with a projected read DTO; move a materialization point and inspect SQL/row counts; create an N+1 pattern deliberately in the lab and fix it. Run a query plan for one filter/index choice using meaningful synthetic data. Do not infer database behavior from an in-memory fake.

**Variations:** tracking plus `SaveChanges` versus a targeted conditional update; `Include` versus projection. Observe that a set-based update does not automatically refresh already tracked objects. Explain update predicates and affected-row checks.

**Migration exercise:** add an optional field, test existing data, and rehearse rollback in a disposable database. Distinguish application rollback from reversing a data change.

**Gate:** reconstruct a disposable database from migrations; explain generated SQL, constraints, identity tracking, transaction boundaries, and `IEnumerable` versus `IQueryable`; demonstrate persistence and a meaningful integration test. Pair with D2 and the first part of D5.

### B4. First complete change to the real application

**Assignment:** add optional **AccessibilityNotes** to an event. This field is absent at the reference checkout. Recheck before starting; if another change has added it, choose an equally small descriptive field with a real use. Do not begin with payments, seat allocation, or a large `TenantSettings` model.

**Small prerequisite:** trace one authenticated organizer request and inspect how the existing integration fixture creates two organizers belonging to different tenants. Explain where the token and tenant scope come from before using those helpers in your tests. Preserve the existing access boundaries. Full authentication implementation and deeper permission design remain B5.

**Requirements to agree before coding:**

- Organizer staff can create/update the notes for their own events.
- Notes are plain text, optional, with an agreed length limit; decide explicitly what missing, null, and empty mean on update.
- Existing events remain valid. Event-detail responses expose the notes; draft visibility remains restricted according to the existing access model.
- Invalid input is rejected without a partial write; other tenants cannot change the value.
- Reloading and restarting preserve the value. The UI edit/display is a later extension in B11.

**You locate and change:** domain state/update method, request/response contracts, validation, service/repository mapping, EF configuration if needed, migration, and tests. Produce your impact list before opening every matching file. Explain why files you did not change do not need changing.

**Testing reps:** write the behavior cases first, author unit and real-database integration tests at the appropriate boundaries, deliberately break one mapping or validation rule in your branch, observe the failing test, and restore the fix yourself. Add the boundary case you initially missed.

**Gate:** show create -> update -> reload -> public detail, invalid input, old-row compatibility, and cross-tenant denial. Explain the complete diff and SQL. Recheck cache invalidation when this field reaches cached responses. Write your own PR description and run relevant existing regressions. Pair with D3-D4.

### B5. Domain rules, authentication, and multi-tenancy

**Purpose:** make business rules and access decisions visible instead of trusting framework annotations without understanding them.

**First:** reconstruct the existing event transition rules from a plain-language table in your lab. Implement a guarded switch, then an allowed-transition table, using the same cases. Compare to [Event.cs](../src/TicketingPlatform.Domain/Event.cs) afterward. Distinguish request validation from a domain invariant and a database constraint.

**Then trace:** [AuthController](../src/TicketingPlatform.Api/Controllers/AuthController.cs), [AuthService](../src/TicketingPlatform.Application/Services/AuthService.cs), password hashing, token generation/validation, refresh storage, logout, and [TenantResolutionMiddleware](../src/TicketingPlatform.Api/Tenancy/TenantResolutionMiddleware.cs). Treat refresh concurrency as a later revisit after B7.

**You implement:** a small training login using standard framework hashing/token libraries, followed by a resource-ownership authorization exercise. No homemade password hashing or cryptography. In the actual app, design a resource-based authorization handler for one owned-resource operation, retaining its query/ownership protections. This is a .NET mechanism to learn, not permission to assume the existing app lacks ownership checks.

**Tests you write:** anonymous denial; wrong role; own resource; another customer's resource; another tenant's resource; intended public access; intended administrator access; expired/invalid token; refresh/logout behavior. Verify both reads and writes. Client-provided IDs cannot establish authorization.

**Variations:** role policy versus resource-dependent permission; explicit tenant predicates versus global filters in the lab. Then explain why the actual five scopes are needed for a cross-organizer marketplace.

**Gate:** draw login and authorized-request flows; explain signed versus encrypted, access versus refresh, 401/403/404 choices, and why customer ownership differs from organizer tenancy. Prove representative anonymous-access, ownership, and tenant-isolation tests fail when you introduce their defects locally, then restore the code and pass the full relevant suite.

### B6. Async, external calls, and cancellation

**Purpose:** understand work that completes later and failures outside your process. Reference: [C# asynchronous programming](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/).

**You implement:** your own small fake provider and HTTP caller with controllable latency, decline, success, timeout, and cancellation. Start with plain behavior, then use a typed client and a bounded resilience policy. Inspect the existing [PaymentProviderClient](../src/TicketingPlatform.Infrastructure/Payments/PaymentProviderClient.cs) after your attempt.

**Variations:** sequential asynchronous calls versus bounded concurrent calls against the same fake dependency. Record elapsed time and actual concurrency. Compare a CPU exercise with I/O waiting; do not wrap routine EF/HTTP calls in `Task.Run` as an async substitute.

**Explain:** task completion, the await continuation/state machine, exception propagation, cooperative cancellation, and why async does not automatically create a thread. Investigate blocking separately: do not claim every `.Result` necessarily deadlocks in ASP.NET Core; include thread starvation and the role of synchronization context in the explanation.

**Failure reps:** stop the fake provider; cancel a request; return a non-retryable response; simulate a result lost after the provider performed work. Predict actual call count and retry delay before measuring. Never run concurrent EF operations on one shared context as a parallelism experiment.

**Gate:** demonstrate cancellation and bounded concurrency; explain which failures deserve retry and why a timeout does not establish that an external write failed. Pair with D6's process/network exercises.

### B7. Holds and concurrency: build all three approaches

**Purpose:** learn the difference between correct sequential code and correct shared-state behavior.

**You implement in stages:**

1. Single-threaded hold/release/expiry behavior with an injected clock and explicit state transitions.
2. A deliberately unsafe counter experiment with coordinated competing tasks; reproduce a lost update without relying only on arbitrary sleeps.
3. A process-local synchronization variant; then demonstrate its limit using two processes.
4. PostgreSQL optimistic concurrency with bounded retry.
5. PostgreSQL pessimistic locking/transaction behavior.
6. Redis arbitration with the PostgreSQL consistency boundary, compensation, and a documented crash gap.

Use the same acceptance cases and workload across alternatives. Read the corresponding [reservation strategies](../src/TicketingPlatform.Infrastructure/Reservations/OptimisticReservationStrategy.cs) only after your attempt at each approach. Different strategies can live in separate lab branches.

**Measure:** requested quantity, successful holds, rejected/conflicting attempts, retries, latency, final inventory, and persisted hold quantities. Start small, then increase contention. Check conservation of capacity for the exact states in your scenario, not only an HTTP success count. A hold benchmark remains distinct from a paid-order benchmark.

**Race cases:** last capacity; two releases; release versus expiry; rollback after decrement; later, checkout versus expiry. Use isolated fixture data. With Redis, kill the process between arbitration and database commit and explain how the stores can drift even when database constraints prevent overselling.

**Gate:** demonstrate correctness and tradeoffs for all three strategies, explain affected rows/concurrency tokens/lock scope, and identify what happens under two replicas. Revisit refresh-token rotation and ticket scan races with the same reasoning. Pair with D3's split-process experiment.

### B8. Checkout, refunds, and uncertain outcomes

**Purpose:** own the complete general-admission purchase state machine.

**Before coding:** draw the hold/order/refund states and mark every database commit and external provider call in [OrderService](../src/TicketingPlatform.Application/Services/OrderService.cs). Explain the difference between a hold TTL and a payment reconciliation lease.

**You implement in your lab:** a happy-path-only checkout, then a recoverable version with a durable claim/order, stable idempotency key, provider inquiry, and a reconciliation loop. Retain standard provider libraries where appropriate; you write the orchestration and tests.

Keep the fake provider's recorded outcomes alive across application restarts, using a separate process or durable fixture store. The embedded development provider stores its records in process memory, so restarting it together with the application cannot prove recovery against a provider that remembers a completed charge.

**Tests:** duplicate request with the same key; incompatible reuse of a key; provider decline; response lost after charge; crash before/after final database commit; two simultaneous finalizers; expired hold; retry/refund race. For each, predict durable state, provider call count, stock, and the next recovery action.

**Product change:** choose one missing or insufficiently tested recovery behavior after inspecting the existing suite. If no behavior change is justified, independently reproduce a focused recovery component and use it to review the current code. Do not invent a payment policy merely to generate a diff.

**Gate:** recreate a small recovery flow from requirements, prove it against controlled failures, and explain why ambiguous payment cannot immediately release stock. Explain the provider idempotency assumption and why repeated HTTP requests do not imply repeated money movement.

### B9. Messaging, outbox, and background workers

**Purpose:** understand business changes that outlive the request that started them.

**You implement:** first an in-process channel producer/consumer, then a small RabbitMQ publisher/consumer, then a PostgreSQL outbox written with a business change. Add acknowledgment, deduplication, bounded retries, poison-message handling, and cancellation one concern at a time. Use small synthetic events, not real notifications to people.

**Trace afterward:** [OutboxWriter](../src/TicketingPlatform.Infrastructure/Outbox/OutboxWriter.cs), [OutboxDispatcher](../src/TicketingPlatform.Infrastructure/Messaging/OutboxDispatcher.cs), [OutboxPublisher](../src/TicketingPlatform.Infrastructure/Messaging/OutboxPublisher.cs), [ConsumerRetryPolicy](../src/TicketingPlatform.Infrastructure/Messaging/ConsumerRetryPolicy.cs), and [TicketIssuerConsumer](../src/TicketingPlatform.Infrastructure/Messaging/TicketIssuerConsumer.cs).

**Failure reps:** crash after business commit/before publish; publish confirmation lost; duplicate delivery; consumer commit before acknowledgment; malformed payload; unsupported event version; dependency failure until retry exhaustion. Record where the message and the business effect end up.

**Worker reps:** implement a cancellable `BackgroundService` that creates a scope for database work; run two copies and observe duplicated scheduling. Compare a local timer with durable work stored in the database. A separate worker deployment does not by itself make every scheduled action single-execution.

**Gate:** prove recovery and idempotent effects, explain at-least-once delivery and the dual-write problem, and justify when a channel is enough and when a broker/outbox is warranted. Pair with D3 and D7.

### B10. Cache, read models, SignalR, and waiting room

**Purpose:** improve read/load behavior while understanding staleness and distributed coordination.

**You implement:**

1. A measured no-cache read, then cache-aside for a safe event read, with key ownership, expiry, and write invalidation.
2. A process-memory variant and a Redis variant; compare one process with two. Test cache outage and simultaneous misses. Explain why TTL jitter alone is not a complete single-key stampede solution.
3. A tiny persisted availability projection, then replay/rebuild it from its defined source. Show lag and duplicate-event behavior. Read models need an explicit source of truth.
4. A SignalR update for your own small feature; test reconnect/poll fallback and two replicas with a backplane.
5. **Optional implementation extension:** a simplified queue followed by shared atomic admission. Inspect the actual Lua/token-bucket implementation after predicting how two admitters can multiply an ordinary per-process rate. Tracing and operating the existing waiting room is required; independently rebuilding it can wait until after your first independently delivered feature.

**Actual-system exercise:** follow a hold through outbox, projection consumer, hub broadcast, and browser update. Identify which reads still query inventory. Verify purchasing rechecks authoritative state despite stale displayed availability.

**Queue investigation:** compare an unbound visitor identifier with the current first-use customer binding and quotas. Include expiry and pre-binding misuse when explaining its limits. A signed-entry-token implementation is an optional later security exercise. Do not describe a GUID as proof of identity.

**Gate:** show measurable read behavior, invalidation, projection recovery, and cross-replica updates in your own exercises. Trace the existing waiting-room admission flow and observe its shared budget. Explain where each added component is unnecessary. New queue/security implementation is not a prerequisite for this gate.

### B11. File storage and enough frontend to own the journey

**Purpose:** connect your backend work to a browser and a downloadable artifact.

**You implement:** finish AccessibilityNotes in the organizer form and public event detail, including loading/error states and validation feedback. Update TypeScript contracts and BFF forwarding yourself. Inspect browser network calls, server-side calls, cookies, and API logs. Explain where the token is handled and which requests actually pass through the BFF.

**Storage exercise:** implement a small `IFileStorage`-style adapter against a local directory, then object storage, with the same contract cases. Trace ticket generation, database metadata, authorization, and streaming. Test an unauthorized download, an invalid path, a missing object, and a retried write. Inspect file cleanup separately from database cleanup.

**Journey you test:** organizer publishes -> customer finds -> hold -> checkout -> pending/confirmed -> PDF -> admission validation; also refund and duplicate-scan cases. Author one browser test for your own feature and explain which failures belong in integration tests instead of many slow browser tests.

**Gate:** demonstrate your field across database/API/BFF/UI, diagnose a contract mismatch, and explain the issued-ticket path and ownership checks. Run relevant web typecheck/lint/build and browser checks yourself. Deep frontend design is optional; owning the request boundary is required.

### B12. Independent product work and architecture judgment

**Purpose:** move from guided reproduction to delivering a new slice with your own design.

**First independent slice:** a minimal `TenantSettings` capability, initially a small non-financial setting such as branding display text or default locale. Define who can read/write it, the default for existing tenants, validation, migration, caching, and tests. Add policy or financial settings only when their behavior is specified and you can test their effect.

**Second slice:** dedicated organizer performance scheduling against the existing model. Define per-date edits/cancellation, tenant ownership, public visibility, and what happens to existing holds/orders. Do not casually change dates on tickets already sold.

**Architecture comparison:** implement one small read/use case as a vertical slice in the lab, compare it with the application's service/repository layering, and write the tradeoff yourself. Keep the modular monolith unless a concrete requirement justifies a service split.

**Optional advanced capstone:** venue administration -> price zones/allocations -> seat-specific holds -> seat selection/checkout. Split each into a reviewable feature. Enforce exclusive live ownership per performance/seat in the database, define expiry transitions, and test contention and rollout compatibility. This is later practice, not your starting assignment or a prerequisite for completing the general-admission learning path.

**Gate:** from a short requirement you independently produce a design, implementation, tests, migration, operational checks, and a reviewable PR. The coach reviews the result and asks questions; you make every correction. Pair with D5-D9.

## 6. DevOps track: you operate what you build

DevOps starts at B0. Do not postpone it until "after the backend," and do not begin by memorizing the existing Kubernetes YAML. The target is to understand and recover your application. Tool selection is secondary.

### D0. Shell, Git, processes, and networking basics [with B0]

Write your own PowerShell runbook. Practice paths/working directory, environment variables, exit codes, stdout/stderr, process IDs, ports, file permissions, and reading logs. Create a branch, inspect a diff, make a small commit, and safely undo your own disposable change. Learn a conflict-resolution exercise separately from product work.

Draw host port 5000 -> API container port 8080 and host port 5433 -> PostgreSQL container port 5432. Explain why `localhost` inside a container identifies that container. Distinguish DNS failure, connection refusal, timeout, HTTP 404, and HTTP 401. Use `localhost` for the web dev origin, and understand the local cookie setting rather than changing it blindly.

**Gate:** find the process serving your request, stop only your process, diagnose a wrong address, and repeat basic file/process inspection in an existing Linux container or WSL environment. PowerShell remains your normal host shell.

### D1. Reproducible local startup [with B0-B3]

Run the API on the host with container dependencies, then run the full Compose application. Record the differences in addresses and configuration. Write your own sequence to bootstrap disposable data, run migrations, start services, make a smoke request, and stop them. Record actual prerequisite failures and fixes yourself.

**Gate:** restart from your own runbook and explain which process applies migrations, which seeds data, and which services have durable state. A response from the API root is not your readiness check; use the documented health endpoints and an actual business request.

### D2. Build a container you understand [after B2; deepen with B4]

Write a Dockerfile for your small lab API before comparing with the repository [Dockerfile](../Dockerfile). Try single-stage and multi-stage variants, inspect final image contents, and observe cache reuse after source and dependency changes. Run as a non-root user and diagnose a deliberately unwritable application directory using disposable paths. Repeat the explanation for the [web Dockerfile](../apps/web/Dockerfile). Reference: [Docker getting started](https://docs.docker.com/get-started/).

**Gate:** explain every instruction, image versus container, build versus runtime configuration, published ports, writable paths, and why a smaller final image is useful. Produce one working image yourself.

### D3a. Compose, service boundaries, and storage [with B4]

Author a local learning overlay rather than copying an unexplained full stack. Start with web/API/dependencies. Diagnose service DNS and server-side versus browser-side API addresses. Compare container replacement with volume persistence. Use disposable fixtures when testing data loss; deleting volumes is not a normal troubleshooting reset.

**Early gate:** explain addresses, startup order versus readiness, and durable volume versus container layer. Reproduce your own Compose setup and retain fixture data across application-container replacement. This is the D3 requirement for checkpoint B.

### D3b. Separate API and worker processes [after B9]

Introduce explicit API and worker roles, since base Compose currently combines them. Stop the worker while leaving the API available, observe persisted backlog and delayed tickets, then restart it and observe recovery.

**Later gate:** explain combined versus split hosting and what a worker outage does to a purchase. Demonstrate the exact failure/recovery rather than only showing running containers. Assess this alongside the messaging work, not before learning what an outbox is.

### D4. CI that you authored [with B4]

Run each intended quality check manually first. Then write a small workflow for your learning branch and compare with [ci.yml](../.github/workflows/ci.yml). Add backend tests, real-database integration, web checks when applicable, and migration parity step by step. Learn jobs, runners, dependencies, artifacts, caches, permissions, secrets, and exit codes. Reference: [GitHub Actions fundamentals](https://docs.github.com/en/actions/get-started/understand-github-actions).

Deliberately fail a behavior test, introduce a model/migration mismatch, and introduce a web type error in disposable commits. Prove the relevant check catches each and fix them yourself. Compare a simple serial workflow with parallel independent checks.

**Gate:** your pipeline catches the intended defects and you can explain every command and failure. Distinguish a scan that reports findings from a blocking policy: current Trivy steps use exit code 0. Current CI publishes images but does not constitute an automatic deployment pipeline.

### D5. Releases, configuration, and schema changes [after B3-B4]

Build an immutable version/SHA-tagged image. Write a local release procedure with preflight checks, reviewed migration SQL or an EF migration bundle, explicit migration execution, startup, smoke checks, and application rollback. Try reviewed SQL and bundle variants on the same disposable change.

Create a learner-controlled Production-configured local profile. Separate migration/seeding from application startup and inject its configuration yourself. Existing Compose/Kubernetes development settings are not a production release procedure. Learn secrets handling and why browser-visible `NEXT_PUBLIC_*` values have different exposure/build timing from server runtime settings.

The embedded `/dev-payment` routes disappear outside Development. Start this release lab with health/read smoke checks. Before adding checkout smoke tests, configure your separate B6 fake provider and deliberate fixture-account setup; do not weaken the Production guards to make the development defaults work.

Rehearse an additive migration with old/new application compatibility. Study the existing performance expand/migrate/contract history afterward. Explain when a destructive change makes rolling back only the image unsafe and when restoring data is required.

**Gate:** release and roll back a compatible application version while preserving fixture data; explain the database compatibility window and demonstrate an actual smoke test.

### D6. Linux, HTTP proxies, and termination [start after D2]

Inspect Linux paths/case, users, permissions, environment, DNS, resource use, and termination signals inside your own local environment. Observe graceful cancellation during shutdown. Introduce one local reverse proxy and local TLS exercise once direct HTTP works; follow a request through proxy and application logs. Configure trusted forwarding deliberately, then test a spoofed forwarding header from an untrusted client.

**Gate:** diagnose a permissions/configuration/network failure, explain listen address versus published address, and show clean shutdown plus recovery of durable work. A local VM and service-manager variant is optional; containers are sufficient for the core path.

### D7. Local Kubernetes from your own manifests [after D3 and B7-B9]

Choose one local cluster tool. Author a namespace, one Deployment, and one Service for your lab before comparing with [k8s](../k8s/kustomization.yaml). Add configuration, secrets, readiness/liveness, resource requests/limits, and persistent storage in separate steps. Reference: [Kubernetes basics](https://kubernetes.io/docs/tutorials/kubernetes-basics/).

Then run the application with one and two API replicas plus workers. Replace an API pod during a real request flow, observe rollout and rollback, and test cross-replica auth, inventory, and SignalR behavior. Readiness controls traffic; it does not automatically pause a background worker.

The supplied [PostgreSQL manifest](../k8s/postgres.yaml) uses disposable `emptyDir` storage. Replacing that database pod loses its data. Introduce a PVC in your learning environment and use only expendable fixtures for destructive drills. A PVC is still not a backup.

**Gate:** draw the network/process/storage map, diagnose a failed rollout, demonstrate invariant-preserving multi-replica behavior, and explain probes without confusing them. `replicas: 2` alone is not correctness evidence.

### D8. Observability for a feature you wrote [start B4; deepen after B9]

Add one useful structured log, counter or histogram, and trace span to your own feature. Query them and create one dashboard panel yourself before exploring all existing dashboards. Trace a request through one asynchronous boundary. Learn sensitive-data avoidance and bounded metric labels. Reference: [OpenTelemetry for .NET](https://opentelemetry.io/docs/languages/dotnet/).

Inject a controlled delay/outage, predict its telemetry, and diagnose it. Write an alert tied to a user-visible symptom. Configure a local receiver you control; existing Alertmanager configuration does not deliver notifications externally. Compare diagnosis from logs alone with your measurements/traces.

**Gate:** explain logs versus metrics versus traces, follow one correlation across components, and prove your alert fires and resolves. Record what monitoring cannot tell you. Pair with the existing [observability plan](OBSERVABILITY_PLAN.md), treating its larger wishlist separately from delivered configuration.

### D9. Backup, restore, and incident response [after B8-B11 and D5]

Write your own backup/restore scripts and runbook for synthetic PostgreSQL data and the object files its ticket records reference. Restore into a separate database/bucket/environment. Verify actual business records, ownership, and a downloadable ticket, not just a successful restore command.

Run short local drills: broker unavailable; worker stopped after committing a business change; uncertain provider result; Redis unavailable; wrong secret/configuration; failed migration; missing object. State the expected behavior of the selected hosting role before the drill. Change one failure at a time and restore the environment yourself.

Measure recovery time and explain potential data loss. Write an incident timeline, evidence, diagnosis, recovery steps, and one justified prevention change. A later optional exercise is recovering one tenant from a shared-schema backup without overwriting other tenants.

**Gate:** restore a usable system and explain what your backup omits. Demonstrate one incident from symptom to recovery using your own runbook, without AI operating the tools.

## 7. Implementation variations: how to make repetition useful

For every comparison, keep the same requirements and test data, change one design choice, measure where appropriate, and write when you would choose each. Reimplementing the same method three times without a new observation does not add much experience.

| Comparison | What you should learn | Required depth |
|---|---|---|
| Loop / LINQ | Execution order, readability, deferred work | Both, B1 |
| Class / record | Mutation, equality, copying, DTO versus entity | Small examples, B1 |
| Controller / Minimal API | Binding, routing, organization, testing | One equivalent endpoint, B2 |
| Direct persistence / service and port / vertical slice | What separation costs and protects | Small lab, then B12 reflection |
| Tracking / projection / conditional update | SQL shape, memory/tracking, affected rows | Targeted B3 experiments |
| Unit fake / real PostgreSQL integration | Fast rules versus actual persistence behavior | Both throughout; never substitute a fake for contention evidence |
| Sequential async / bounded concurrent I/O | Latency, pressure, cancellation | Both, B6 |
| Optimistic / pessimistic / Redis | Contention, retries, locks, cross-store recovery | All three, B7 |
| In-process channel / broker with outbox | Process lifetime versus durable delivery | Both, B9 |
| No cache / memory cache / Redis | Measured benefit, staleness, replicas | One safe read, B10 |
| Local files / object storage | Storage contract, consistency, access and lifetime | Same adapter tests, B11 |
| Single-stage / multi-stage image | Build tools, layers, cache, runtime footprint | Small image, D2 |
| Combined / separate API and worker | Independent operation and scheduling | Local experiment, D3 |
| Reviewed migration SQL / migration bundle | Deployment artifact and control | Disposable schema, D5 |
| Compose / Kubernetes | Networking, lifecycle, readiness, rollout | Operate both; explain when Compose is enough |

Do not add microservices, a search cluster, native mobile apps, service mesh, or multiple cloud platforms to satisfy this learning plan. A paid deployment and infrastructure-as-code are optional later exercises after you can operate and recover locally. Pick a real requirement and a budget before adding them.

## 8. Progress, pacing, and checkpoints

Use three types of session: build a small behavior; diagnose/test it; explain and repeat it. Begin each session by identifying one concrete deliverable. End by recording evidence and the next small step. Avoid assigning yourself an entire stage as a weekend task.

Suggested weekly balance, adaptable to your availability: roughly half implementation, one quarter debugging/testing, and one quarter explanation/DevOps. These are planning suggestions, not deadlines. Estimate larger stages only after the first few sessions show your actual pace.

Use these checkpoints instead of the old project's completed phase labels:

| Checkpoint | Required demonstration |
|---|---|
| A: fundamentals | B0-B3, D0-D2: a small API you wrote, persistence you inspected, tests you designed, and a container you understand |
| B: owned feature | B4-B5, D3a-D4: a full real-app change with validation, authorization, migration, tests, and your CI |
| C: reliable purchase | B6-B9 and D3b: reservation races, provider uncertainty, durable checkout, outbox/recovery, explained and reproduced |
| D: operated system | B10-B11 and D5-D9: browser journey, storage, deployment, telemetry, rollback, and restore |
| E: independent delivery | B12: a bounded new requirement designed, implemented, tested, deployed locally, and defended with limited hints |

Interview explanation practice begins at A, not after every optional feature. At B, start practicing small take-home-style changes from requirements. At C/D, add timed system-design and incident exercises. Use weak explanations to choose the next rep; do not add another framework to avoid that gap.

### Learner ledger

Everything below starts unassessed. You fill the evidence and status yourself. "Present in source" and "I can build it" are separate statements.

| Stage | Observed | Implemented by me | Tests/failure evidence | Explained + cold repeat | Commit/date |
|---|---|---|---|---|---|
| B0 Tools/map | Pending | Pending | Pending | Pending | |
| B1 C# | Pending | Pending | Pending | Pending | |
| B2 HTTP/DI | Pending | Pending | Pending | Pending | |
| B3 SQL/EF | Pending | Pending | Pending | Pending | |
| B4 Full change | Pending | Pending | Pending | Pending | |
| B5 Rules/auth/tenancy | Pending | Pending | Pending | Pending | |
| B6 Async/integrations | Pending | Pending | Pending | Pending | |
| B7 Concurrency | Pending | Pending | Pending | Pending | |
| B8 Checkout/recovery | Pending | Pending | Pending | Pending | |
| B9 Messaging/workers | Pending | Pending | Pending | Pending | |
| B10 Cache/realtime/queue | Pending | Pending | Pending | Pending | |
| B11 Files/browser | Pending | Pending | Pending | Pending | |
| B12 Independent feature | Pending | Pending | Pending | Pending | |
| D0-D1 Shell/startup | Pending | Pending | Pending | Pending | |
| D2-D3a Containers/Compose | Pending | Pending | Pending | Pending | |
| D3b API/worker split | Pending | Pending | Pending | Pending | |
| D4-D5 CI/releases | Pending | Pending | Pending | Pending | |
| D6-D7 Linux/Kubernetes | Pending | Pending | Pending | Pending | |
| D8-D9 Observe/recover | Pending | Pending | Pending | Pending | |

For each stage, keep one concise explanation, one failure investigation, and the relevant code/config commits. Keep selected comparison results. This is stronger evidence of ownership than an increasing test count with no explanation of what the tests prove.

### Preserve the learning mode in future sessions

Because the older agent files still describe a different workflow, begin a new session with:

> Read docs/HANDS_ON_LEARNING_PLAN.md and follow its coach-only agreement. I write and run all implementation, tests, configuration, and commands. My current exercise is __. Here is my attempt and evidence: __. Explain the next concept and give me one bounded assignment or diagnostic hint; do not implement the solution.

Updating the older agent files to link this agreement can itself be your first small documentation/Git exercise. Do not mark an application milestone incomplete merely because you have not learned it yet; update the learner ledger instead.

### What to bring to an interview or review

Be clear about the inherited AI-built baseline. Demonstrate the parts you personally reconstructed or extended, the failures you diagnosed, and the deployments/restores you operated. Prepare a five-minute system tour, a request trace, a database/concurrency explanation, and one incident story based on those records. Your own evidence is the basis for claiming experience.

**Next action:** Session 1. Inspect the repository and environment yourself, run one anonymous tenant-event request, and return with your output and your own first system map.
