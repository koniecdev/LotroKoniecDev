# ADR-0035: Signal-driven outbox relay instead of interval polling

**Status:** Accepted
**Date:** 2026-08-02
**Decision-makers:** Solo maintainer
**Related:** AuthSystem messaging (rabbitmq-introduction branch), `OutboxRelay`, `RegisterUser`, ADR-0025 (Neon probe burn), ADR-0034 (Hetzner hosting), `docs/knowledge-base` Neon PITR topology notes

## Context

The auth outbox (`OutboxMessages`, written by `RegisterUser.cs` inside its registration
transaction) needs a relay that publishes committed rows to RabbitMQ. The textbook relay is a
`BackgroundService` polling every few seconds — and on this stack that design is not a style
choice but a budget decision:

- Production databases are **Neon Free tier, scale-to-zero**: 100 CU-h/month per project,
  compute suspends after ~5 min without queries and bills a 0.25 CU minimum while awake.
- Any poll interval **below** the suspend threshold keeps compute awake 24/7:
  0.25 CU × 730 h ≈ **180 CU-h/month** — the cap blows mid-month with zero users. Intervals
  **above** the threshold pay ~0.02 CU-h per wake-up (one query resurrects compute for the
  ~5-min suspend window): a 10-min poll ≈ 90 CU-h/month, 30-min ≈ 30 CU-h/month. Polling only
  becomes affordable at multi-hour cadence, which defeats "the confirmation e-mail arrives now".
- ADR-0025 already documented this failure class (a health probe silently burning compute);
  #407 proved such leaks stay invisible for days.
- The decisive topology fact: **the outbox writer and the relay live in the same process**
  (auth-api, single replica per ADR-0034's one-box compose deploy — no Kubernetes, no scale-out
  planned). The process that commits an outbox row can wake the relay in memory, for free, at
  the exact moment the database is provably awake (it just served that write).
- The delayed-delivery tail is tolerable specifically for today's only message type:
  `EmailConfirmationRequested`'s user-facing recovery path, `ResendEmailConfirmation.cs`,
  bypasses the outbox entirely (direct SMTP), so a stuck row never strands a user.
- One live trap: `RegisterUser` commits via an **explicit transaction**
  (`SaveChangesAsync` → `transaction.CommitAsync`). Any wake-up hooked to `SaveChanges` (e.g. a
  naive `SaveChangesInterceptor`) fires before the commit, the relay reads nothing, and the row
  orphans until the next sweep.

## Decision

### 1. The relay is woken by an in-process signal, not a timer

`OutboxSignal` (singleton, a 1-slot `SemaphoreSlim`) is the wake-up line. The relay
(`OutboxRelay`) blocks on `WaitAsync` and drains the whole backlog per wake-up. No
`PeriodicTimer`, no fixed poll interval exists to tune. Repeated notifies coalesce into one
pending wake-up because a drain pass reads everything anyway.

### 2. Writers notify after the commit — this is a binding convention

Every outbox writer calls `OutboxSignal.Notify()` **after its transaction commits**, never
inside it (see Context: the `RegisterUser` explicit-transaction trap). With one writer today the
call is explicit in the handler. If writers multiply (≥3), promote the convention to a
`SaveChangesInterceptor`+`IDbTransactionInterceptor` pair via a follow-up ADR; until then the
explicit call is the YAGNI-correct form. A forgotten notify is a soft failure: the row waits for
a sweep instead of being lost.

### 3. Two sweeps bound the orphan window

- **Startup sweep:** the relay's first pass runs before any wait, catching rows whose nudge died
  with the previous process (crash between commit and publish, deploy restart).
- **Safety sweep:** the signal wait times out every **6 hours** and sweeps anyway. Cost ceiling
  ≈ 120 wake-ups/month × 0.02 CU-h ≈ 2.5 % of the Neon budget; latency ceiling for a fully
  orphaned row: 6 h. Tighter bounds buy latency no current message type needs.

### 4. Failure backoff escalates and caps at the sweep cadence

A pass with any failed row (broker down, unroutable type, DB fault) waits
30 s → 1 m → 5 m → 30 m → 6 h instead of the full sweep interval; a fresh notify overrides the
wait at any time. The ceiling deliberately equals the safety-sweep interval so a poison message
degenerates into the already-priced sweep cadence, not an around-the-clock retry poll.

### 5. Outbox `Type` maps to routing keys explicitly

`OutboxMessageRouting` translates the payload contract name (`Type`, e.g.
`nameof(EmailConfirmationRequested)`) to its `RabbitMqTopology` routing key
(`email.confirmation`). An unmapped type is marked failed with a diagnostic `LastError` and
retried on sweep cadence — it never reaches the broker as an unroutable mandatory publish.

### 6. Delivery semantics stay at-least-once, per-row commit

The relay marks each row (`MarkAsProcessed`/`MarkFailed`) and saves **per message**, narrowing
the crash window in which an already-published message is re-published after restart. Consumers
must deduplicate on the AMQP `message-id` (= outbox row id) and tolerate reordering after
retries; nothing in this design promises ordering across failures.

## Consequences

### Positive

- Idle cost is **zero**: no background DB traffic exists, Neon suspends normally. Every relay
  query lands milliseconds after a write, while compute is already awake.
- Publish latency in the happy path is milliseconds (commit → notify → drain), better than any
  affordable poll interval.
- The orphan tail is bounded (6 h) and, for the only current message type, user-recoverable via
  the outbox-independent resend path.
- No new infrastructure: one semaphore, one hosted service, both already in-process.

### Negative / Accepted Trade-offs

- **Hard single-replica assumption.** A second auth-api replica (or an extracted relay) breaks
  this silently: duplicate publishes from concurrent sweeps (no `FOR UPDATE SKIP LOCKED`), and
  rows written by a dead replica wait for another replica's sweep. Scaling out requires
  revisiting this ADR (LISTEN/NOTIFY + row locking is the upgrade path). Accepted: replicas are
  explicitly out of scope for this deployment.
- **Unbounded-ish latency tail vs. polling's 5 s worst case.** "Usually instant, worst case 6 h"
  is the price of the budget; acceptable while every message type has a user-driven fallback or
  no urgency.
- **Convention, not mechanism.** Writers must remember the after-commit `Notify()`; raw-SQL or
  out-of-band inserts (psql debugging) wait for a sweep. Mitigated by the sweeps and by rule 2's
  escalation trigger.
- **Broker outages re-awaken the database** on the retry backoff for their duration — a long
  RabbitMQ outage costs both undelivered mail and some CU-h. Capped by the 6 h ceiling.
- The design's reason lives in Neon economics; on a paid tier or self-hosted Postgres it looks
  like over-engineering and someone will be tempted to "simplify" it back to polling. This ADR
  is the guard.

## Alternatives Considered

### A. Interval polling (`PeriodicTimer`, 5 s–2 min)

The textbook relay. Any interval below Neon's ~5-min suspend threshold keeps compute awake
24/7 (~180 CU-h/month vs. the 100 CU-h cap); intervals above it still pay ~0.02 CU-h per empty
wake-up and push e-mail latency to the same order as the safety sweep anyway. There is no
interval that is both responsive and affordable. Rejected.

### B. Quartz.NET scheduling the poll

Adds a scheduler library to run the same poll, and with a persistent (`QRTZ_*`) job store Quartz
itself polls the database for due jobs and cluster locks — a second compute-burner managing the
first. With an in-memory store it offers nothing a semaphore lacks and still dies with the
process. Rejected.

### C. Postgres `LISTEN/NOTIFY`

Correct cross-process wake-up — but it requires a standing connection, Neon drops connections on
suspend (subscription lost, reconnect loop required), and it only pays off once writer and relay
live in different processes, which contradicts the current topology. Noted as the upgrade path
if replicas ever arrive. Rejected for now.

### D. CDC via Debezium (WAL / logical replication)

The enterprise outbox answer (Debezium even ships an outbox event router). Disqualified three
times over: Neon disables scale-to-zero entirely while logical replication is active (the full
~180 CU-h/month bill back); an idle replication slot retains WAL against the 0.5 GB Free storage
cap (currently ~0.37 GB used — days to overflow); and a JVM connector container on 4 GB Hetzner
boxes is infrastructure disproportionate to a near-zero message volume. Rejected.

### E. Pay: Neon Launch tier, or self-host Postgres on the VPS

$19/month (300 CU-h) or a Postgres container beside the apps would make plain polling fine.
Viable, deliberately deferred: pre-launch there is no revenue to justify the spend, and
self-hosting forfeits Neon PITR/branching that MIGR-04 and the N-1 gates lean on. If either
happens later, revisit this ADR before "simplifying" the relay. Rejected for now.

## Implementation Notes

- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Outbox/OutboxSignal.cs` — the 1-slot semaphore
  wake-up line (singleton).
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Outbox/OutboxMessageRouting.cs` — `Type` →
  routing-key map over `RabbitMqTopology`.
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/BackgroundServices/OutboxRelay.cs` — startup
  sweep, signal wait with 6 h timeout, batched drain (100/fetch over
  `IX_OutboxMessages_Unprocessed`), escalating backoff, per-row mark+save.
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Features/Auth/RegisterUser.cs` — after-commit
  `Notify()` (the binding convention of decision 2).
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/ApiDependencyInjection.cs` — singleton signal +
  hosted relay registration.
- Tests: `OutboxSignalTests`, `OutboxMessageRoutingTests` (unit);
  `OutboxRelayTests` + `SpyMessagePublisher` (integration, real PostgreSQL, no broker).

## References

- ADR-0025 — the prior Neon compute-burn incident class ("never tag a DB check ready").
- ADR-0034 — Hetzner single-box compose topology (the single-replica premise).
- ADR-0023 / ADR-0024 — migration gates that lean on Neon PITR (why self-hosting isn't free).
- `docs/deployment/runbook.md` — Neon project/branch topology and CU-h counters.
- Neon docs: scale-to-zero suspend behavior; logical replication disabling scale-to-zero.
