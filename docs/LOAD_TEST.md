# Flash-sale load test: the three oversell-prevention strategies compared

The platform ships three interchangeable `IReservationStrategy` implementations (selected via
`Reservation:Strategy`). This test hammers the single hottest code path — `POST /holds` for one
ticket type with limited inventory — with 100 concurrent buyers fighting over 300 tickets, once
per strategy, and measures what each trade-off actually costs.

## How to reproduce

```bash
# infra up (postgres/redis/rabbitmq), then per strategy:
Reservation__Strategy=PessimisticLock dotnet run -c Release --project src/TicketingPlatform.Api
dotnet run -c Release --project tools/TicketingPlatform.LoadTest -- --label PessimisticLock --workers 100 --capacity 300
```

The harness (`tools/TicketingPlatform.LoadTest`) provisions its own tenant/staff/event through
the public API, fires the storm, and then verifies integrity from the API's live event graph:
`sold == capacity - remaining` and `sold <= capacity`. A run that oversells exits non-zero.

## Results

Local machine (API + Postgres + Redis in Docker + harness on one box), .NET 10 Release,
100 workers, capacity 300, quantity 1 per attempt. Numbers are for RELATIVE comparison
between strategies, not absolute capacity planning.

| Strategy | Attempts | Sold | Conflicts (409) | Sellout time | Sold/s | p50 (all) | p95 (all) | p99 (all) | Oversold |
|---|---|---|---|---|---|---|---|---|---|
| OptimisticConcurrency | 2082 | 300/300 | 1782 | 5.0s | 60/s | 232 ms | 387 ms | 777 ms | 0 |
| PessimisticLock | 300 | 300/300 | 0 | 2.5s | 121/s | 319 ms | 1614 ms | 2055 ms | 0 |
| RedisAtomic | 5738 | 300/300 | 5438 | 3.0s | 100/s | 13 ms | 32 ms | 1165 ms | 0 |

**All three strategies sold exactly 300 of 300 with zero oversell.** That is the non-negotiable;
everything else is trade-off.

## What the numbers say

- **OptimisticConcurrency (xmin retry)** — every attempt does full DB work, and under hot-row
  contention most of it is wasted: 1782 of 2082 attempts (86%) lost the `xmin` race or found no
  stock after retrying. Fine for normal traffic where collisions are rare; under a spike the
  retry churn burns database round trips and the sell rate drops to the slowest of the three.
- **PessimisticLock (`FOR UPDATE`)** — zero wasted work: exactly 300 attempts, 300 sales,
  because every request queues on the row lock and gets a truthful answer. The cost is the
  queue itself: p99 latency hit ~2s at only 100 concurrent buyers, and attempt throughput is
  capped by lock serialization (~120/s). Latency degrades linearly with the crowd — 10,000
  buyers would wait minutes. Right choice when correctness-per-attempt matters more than
  latency (e.g. box office, low concurrency).
- **RedisAtomic (DECRBY gate)** — the flash-sale profile. The atomic Redis counter arbitrates
  BEFORE the database is involved, so the 5438 losers were turned away in ~13ms median without
  ever touching Postgres, and the API absorbed ~1,900 attempts/s — an order of magnitude more
  pressure than the other two — while winners still sold out the stock in 3s. The price is
  two stores that must agree (documented drift window + compensation in the strategy).

## The bug this test caught

The first RedisAtomic run produced **6,206 HTTP 500s**: winners of the Redis `DECRBY` then
mirrored the decrement onto the tracked `Inventory` entity, whose `xmin` concurrency token made
concurrent winners fight *each other* — every loser of that second race threw
`DbUpdateConcurrencyException`. The fix (in `RedisAtomicReservationStrategy`): winners mirror
the decrement with a single atomic `ExecuteUpdate` (`SET available = available - N ... WHERE
available >= N`) inside one transaction with the hold insert — Redis already decided who won,
so the DB write must not re-litigate it. This is exactly the class of defect only a
concurrency load test surfaces; unit and integration tests with a handful of parallel requests
never tripped it.

## Pairing with the waiting room

The strategies bound *contention damage*; the virtual waiting room bounds *arrival rate*. With
the waiting room admitting (say) 5 buyers/s, even OptimisticConcurrency sees almost no
collisions. Strategy choice then covers the traffic the valve lets through plus everything the
platform serves without a queue.
