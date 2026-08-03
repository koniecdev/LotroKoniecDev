# ADR-0036: Broker-enforced dead-lettering with a quorum-queue delivery limit

**Status:** Accepted
**Date:** 2026-08-03
**Decision-makers:** Solo maintainer
**Related:** ADR-0035 (signal-driven outbox relay), ADR-0034 (Hetzner one-box compose deploy),
`RabbitMqTopology`, `EmailConfirmationConsumer`, `docs/adr/0035` §6 (at-least-once semantics)

## Context

The rabbitmq-introduction branch gave the confirmation e-mail an outbox → broker → consumer
pipeline, but both consumer failure paths ended badly:

- **Poison** (unreadable payload): `basic.nack` with `requeue: false` — and without a dead-letter
  exchange the broker **deletes the message permanently**. The log line said "dropped loudly";
  the data was still gone.
- **Transient** (SMTP down): a 30 s in-process pause, then `basic.nack` with `requeue: true` —
  an **unbounded** retry loop. A permanently failing send (broken template, upstream rejecting
  the sender) would grind the same message forever.

Constraints that shape the fix:

- **At-least-once end to end (ADR-0035 §6):** the relay may re-publish after a crash, so the
  safety net must never turn "retry later" into "lost".
- **Solo-maintainer ops on a one-box compose deploy (ADR-0034):** the failure parking lot must
  be inspectable with what already runs (management UI, logs) — no new services, no extra
  consumers.
- **Queue arguments are immutable.** Redeclaring an existing queue with different arguments
  fails the channel with `PRECONDITION_FAILED` — every argument chosen here is a small
  commitment; changing one later means delete + redeclare (dev: `docker compose down -v`).
- **The broker's consumer ack timeout (30 min default)** caps any in-process pause taken while
  a delivery is unacked; exceeding it kills the channel.
- **RabbitMQ ≥ 4.3 counts specifically.** The delivery limit is measured against
  `x-delivery-count`, which increments **only** on `basic.reject` and on connection loss.
  `basic.nack` is an "explicit return" (increments only `acquired-count`) and `basic.get`
  redelivery counts nothing — both redeliver forever without ever tripping the limit. Proven
  empirically by the integration suite: a nack-requeue loop passed 17 000 deliveries with
  `x-delivery-limit: 5` before the assertion gave up.

## Decision

### 1. `emails.send` becomes a quorum queue with a delivery limit

`x-queue-type: quorum`, `x-delivery-limit: 5`, `x-dead-letter-exchange: lotro.emails.dlx`.
The **broker** enforces "1 initial delivery + 5 redeliveries, then park" — the cap holds across
consumer restarts and even for a crash loop, because connection loss increments the same
counter. Classic queues track no redelivery count at all, which would push retry bookkeeping
into every consumer (and a consumer-side count resets on restart).

### 2. Dead letters flow through a fanout DLX into one terminal parking lot

`lotro.emails.dlx` (fanout) → `emails.send.dlq` (quorum). Fanout, because "everything that dies
goes to the one parking lot" needs no routing decisions — and dead-lettering preserves the
original routing key on the message, so replay knows where it belongs. Nothing consumes the
DLQ; it carries no delivery limit and no DLX of its own — it is terminal by design.

### 3. The dead-letter hop itself is at-least-once

`x-dead-letter-strategy: at-least-once` (+ its prerequisite `x-overflow: reject-publish`,
inert while no `x-max-length` is set). The default `at-most-once` may drop a dead letter under
pressure — a safety net that can silently lose the very message it exists to keep is not one.

### 4. The consumer rejects; it never nacks

All failure paths use `basic.reject` (see Context: only rejects count). Poison is rejected
without requeue on first sight — no redelivery fixes an unreadable payload — and parks
immediately with `x-death` reason `rejected`. Transient failures are rejected with requeue
behind an **escalating in-process pause** (30 s → 2 m → 5 m → 10 m → 15 m, one entry per
allowed redelivery, each far under the ack timeout): the ladder in total outlasts a realistic
SMTP outage (~33 min) before the message parks with reason `delivery_limit`. Pausing in-process
blocks the (prefetch-1) consumer deliberately — every message in this queue needs the same SMTP
relay, so none of the waiting ones could succeed either. The final allowed attempt is announced
at `Error` level before the parking reject.

### 5. Replay is manual

Ops story: inspect `emails.send.dlq` in the management UI, fix the cause, re-publish to
`lotro.emails` under the message's preserved routing key (shovel or UI). No replay endpoint, no
automated re-drive — YAGNI at the current volume; the user-facing recovery for the only current
message type is the outbox-independent resend endpoint (ADR-0035).

## Consequences

### Positive

- **No silent loss anywhere:** poison parks instead of vanishing; exhausted retries park
  instead of looping; the park hop itself is at-least-once.
- **Every failure loop is bounded** — including consumer crash loops — by one broker-enforced
  counter shared with the backoff ladder (`RabbitMqTopology.EmailDeliveryLimit`).
- ~33 min of SMTP outage rides out on backoff alone, no human needed; beyond that the message
  waits intact in the DLQ.
- The whole mechanism is topology + one consumer — no new infrastructure (fits ADR-0034 ops).

### Negative / Accepted Trade-offs

- **The in-process pause blocks the single consumer.** Accepted: shared SMTP dependency (see
  Decision 4). If a second message type with a *different* downstream ever shares this queue,
  give it its own queue instead of revisiting the pause.
- **Nobody watches the DLQ yet.** Parking emits an `Error` log (2332/2339), but there is no
  alert on DLQ depth; that belongs to the prod broker config (monitoring/alerting) planned next.
- **Version-sensitive semantics.** The reject-vs-nack counting rule is RabbitMQ ≥ 4.3 behavior;
  the integration suite pins it against the same image generation compose runs, so a broker bump
  that changes the rules fails loudly in CI, not in prod.
- **Argument immutability makes this a one-shot declare.** Existing dev brokers must
  `docker compose down -v` (or delete the queues) once; documented here and in the declaration's
  remarks. No prod broker exists yet, so the cost is zero today.
- `at-least-once` dead-lettering holds unconfirmed dead letters in memory until the DLQ accepts
  them; harmless at this volume, revisit if a high-throughput queue ever adopts this recipe.

## Alternatives Considered

### A. TTL retry-queue ladder (retry queue with per-message TTL, DLX back to the work queue)

The textbook non-blocking backoff: rejected messages wait in a side queue whose TTL returns
them. Rejected: two more queues + an exchange whose TTLs are frozen into immutable queue
arguments (every cadence tweak = delete + redeclare), to buy non-blocking behavior that buys
nothing here — the consumer it would unblock has nothing else it could usefully do (Decision 4).

### B. Delayed-message exchange plugin

A community plugin adding a broker capability for scheduled delivery. Rejected: new operational
surface (plugin lifecycle on every environment) for the same non-benefit as A.

### C. Consumer-side retry counting (parse `x-death`, or republish with an attempts header)

Rejected: the broker already keeps the authoritative counter (`x-delivery-count`); a republish
mints a new message identity, which breaks `message-id`-based deduplication (ADR-0035 §6) and
moves the loss window into application code — the exact class of bug this ADR removes.

### D. Rely on the 4.x default delivery limit (20) instead of an explicit argument

Rejected: implicit, version-dependent, and disconnected from the backoff ladder; an explicit
shared constant keeps "allowed redeliveries" and "pauses between them" provably in step.

### E. Status quo (no DLX, unbounded nack-requeue)

Rejected outright: deletes poison messages and loops transient failures forever — both
disproven by the guarantees above.

## Implementation Notes

- `RabbitMqTopology` — names + `EmailDeliveryLimit` (single source of truth, shared by the
  declaration, the consumer and the tests).
- `RabbitMqTopologyDeclaration` — DLX/DLQ declared **before** the work queue (no window where
  dead letters reach an unbound exchange); argument sets documented in-file.
- `EmailConfirmationConsumer` — reject-only acknowledgement, `RedeliveryBackoffs` ladder,
  final-attempt `Error` log (EventId 2339).
- `RedeliveryCount` — tolerant `x-delivery-count` reader (absent/foreign types → 0; erring
  toward extra patience, never early parking).
- Tests: `DeadLetterTopologyTests` (real broker via Testcontainers — rejected→DLQ with reason
  `rejected` and preserved routing key; exhaustion→DLQ with reason `delivery_limit` after
  exactly limit+1 deliveries; declaration idempotence; ack leaves DLQ empty) +
  `RedeliveryCountTests` (unit). The broker image pins the compose generation
  (`rabbitmq:4.3.4-alpine`).

## References

- RabbitMQ docs: Quorum Queues — delivery limit, `x-delivery-count`/`acquired-count` increment
  table (the ≥ 4.3 reject-vs-nack rule); Dead Lettering — `at-least-once` strategy and its
  `reject-publish` prerequisite.
- ADR-0035 — outbox relay; at-least-once + `message-id` deduplication contract.
- ADR-0034 — one-box compose topology (the ops-surface constraint).
