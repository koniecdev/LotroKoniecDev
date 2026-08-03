# ADR-0037: Consumer-side inbox deduplication on the broker message id

**Status:** Accepted
**Date:** 2026-08-03
**Decision-makers:** Solo maintainer
**Related:** ADR-0035 §6 (at-least-once + the message-id deduplication promise), ADR-0036
(broker-enforced dead-lettering), `OutboxRelay`, `RabbitMqMessagePublisher`,
`EmailConfirmationConsumer`, `EmailConfirmationRequestProcessor`

## Context

The confirmation-e-mail pipeline is at-least-once on **both** hops, by design (ADR-0035 §6):

- The **relay** may re-publish: a crash between `PublishAsync` and `MarkAsProcessed` leaves the
  outbox row unprocessed, and the next pass publishes the same message again.
- The **broker** may redeliver: a crash between processing and the ack returns the delivery to
  the queue, and the consumer sees it again.

ADR-0035 already committed to the standard answer — "consumers deduplicate on the message id"
(`OutboxRelay.PublishOneAsync` remarks) — and the publisher stamps `MessageId` with the outbox
row's Guid, a stable identity across re-publishes. But nothing on the consumer side keeps that
promise yet. The only guard is the processor's *natural* idempotency (redelivery finds
`EmailConfirmed` already set, or at worst re-sends the e-mail), and its own remarks admit the
weakness: every new message type must re-earn that property by business-logic luck.

Constraints that shape the fix:

- **The side effect is an SMTP send.** It cannot join a database transaction, and the SMTP
  protocol carries no idempotency key — so no design can make the send itself exactly-once;
  a design can only shrink the duplicate window and bound the damage.
- **ADR-0036 just made retry, backoff and the parking lot broker-owned** (quorum queue,
  `x-delivery-limit: 5`, DLX → DLQ), proven against a real broker. The inbox must compose with
  that machinery, not rebuild it.
- **Neon scale-to-zero (ADR-0035):** every database touch costs compute, and a cold resume
  takes ~31 s — measured on this very stack. Extra background pollers are the anti-goal;
  touching the database only when a message actually arrived is fine, because the delivery
  itself proves the system is awake.

## Decision

### 1. An `InboxMessages` table records every fully processed message id

`InboxMessages(MessageId uuid PK, ProcessedOn timestamptz)` in the auth database, next to the
outbox. A row means "this message id has been processed to completion; never process it again."
Deliberately no `Type`, no `Payload`, no attempt counters: `MessageId` equals
`OutboxMessages.Id`, so the full context of any inbox row is one join away, and retry
bookkeeping already lives on the broker (ADR-0036) — duplicating either would be dead weight.

### 2. The consumer checks before processing and records after success

`EmailConfirmationConsumer.OnDeliveredAsync` becomes: deserialize → **look up the message id in
the inbox** → on a hit, ack immediately (a duplicate never touches business logic) → on a miss,
run the processor → on success, **insert the inbox row, save, then ack**. Failure paths are
untouched: the reject/backoff/DLQ ladder of ADR-0036 stays the sole owner of retries.

The marker lands **after** the send, not before. Marker-first (a claim) would trade
duplicate-e-mail risk for lost-e-mail risk: a crash between claim and send acks a message whose
e-mail never went out, and a confirmation that never arrives is strictly worse than one that
arrives twice — the user's only recovery is noticing silence and requesting a resend. The
pipeline therefore stays honestly at-least-once, same as both hops before it.

### 3. A delivery without a message id is poison

The publisher always stamps `MessageId`; a message without one (or with a non-Guid value) did
not come from this pipeline and cannot be deduplicated. Processing it "blind" would reopen the
unbounded-duplicate hole this ADR closes, so it is rejected without requeue on first sight and
parks in the DLQ for a human — the same treatment as an unreadable payload.

### 4. Database faults ride the existing transient path

The inbox lookup and insert are database reads/writes and can fail (Neon cold start, outage).
Such failures follow the same route as any transient processor failure: reject with requeue,
counted by the broker's `x-delivery-count`, bounded by the delivery limit. No new failure
handling exists for the inbox — one ladder, one counter, one parking lot.

### 5. One table, one consumer — the discriminator is a documented constraint, not a column

Today exactly one queue and one consumer exist. **A second consumer of the same message (a
fanout binding) must not share this table without adding a consumer discriminator** — two
consumers deduplicating against each other's rows would each silently skip work the other did.
The constraint is recorded here and in the entity's remarks instead of shipping a speculative
`Consumer` column nobody queries (YAGNI, and the migration to add it later is additive).

### 6. No retention sweep

One row per confirmation e-mail, ~40 bytes; the outbox keeps its processed rows too. A
retention sweep would be the first scheduled database work this system runs purely for hygiene
— against ADR-0035's whole point — to reclaim megabytes per decade. Deliberately skipped;
if volume ever materializes, one shared outbox+inbox sweep ships as its own ticket. A pleasant
side effect of keeping rows forever: replaying an already-processed message from the DLQ
(ADR-0036 Decision 5) hits the inbox and acks without a duplicate e-mail, no matter how much
later the replay happens.

## Failure-ordering analysis: why "the database died on vacation" sends zero e-mails

The nightmare scenario — Postgres down for hours, nobody watching, the user's mailbox filling
with retries — is prevented by *ordering*, not just by limits. Before any SMTP traffic, the
consumer performs two database reads: the inbox lookup (Decision 2) and the processor's
`FindByIdAsync` user load. A dead database therefore fails every attempt **before** a single
e-mail leaves:

| Scenario | E-mails sent | Bound |
|---|---|---|
| Database down for the whole episode | **0** — every attempt dies at a pre-send read | ≤ 6 deliveries over ~33 min (ladder), then DLQ |
| Database dies exactly between the SMTP send and the inbox insert, every attempt | 1 per delivery, realistically 2 | hard cap **6** (`x-delivery-limit: 5` + initial) |
| Replay of an already-processed message from the DLQ | **0** — inbox hit, ack | — |

After the retries exhaust, the message waits intact in `emails.send.dlq` until a human returns
and replays it (ack-and-republish, never reject-requeue — ADR-0036 Decision 5). Nothing loops,
nothing is lost, and the reputation-burning flood is structurally impossible: the broker's
delivery limit caps *total* deliveries per message id forever, across restarts and crash loops.

## Consequences

### Positive

- **Deduplication is mechanical, not per-type business luck.** The relay's promise
  ("consumers deduplicate on the message id") is finally kept, and the next message type
  inherits the recipe instead of re-earning idempotency in its handler.
- **The DLQ replay story gets safer:** replaying a processed message becomes a no-op.
- **Zero new infrastructure** — one table, a few consumer lines; retry/backoff/parking stay
  broker-owned (ADR-0036 untouched). Fits the solo-maintainer ops surface (ADR-0034).
- **The database is touched only when a message actually arrives** — no poller, no sweep,
  matching ADR-0035's compute posture.

### Negative / Accepted Trade-offs

- **The send itself stays at-least-once.** A crash in the millisecond window between the SMTP
  send and the inbox insert still duplicates one e-mail (bounded at 6 by the delivery limit).
  Closing that window requires an idempotency key at the provider, which SMTP cannot express.
  If e-mail delivery ever moves to an HTTP API that accepts one, passing `MessageId` closes it
  — recorded here as the designated future move, not built now.
- **One extra read and one extra write per processed message.** Negligible at this volume, and
  incurred only while the database is provably awake.
- **Unbounded table growth**, accepted knowingly (Decision 6).
- **The no-discriminator constraint (Decision 5) is prose, not schema.** A future fanout
  consumer must read this ADR (or the entity's remarks) before reusing the table.

## Alternatives Considered

### A. Full inbox — store-then-ack with a database-side processor

The consumer writes the raw message to the table, acks immediately, and a signal-driven
background processor (a mirror of `OutboxRelay`) does the work with database-side retry state.
Rejected: it re-implements exactly the machinery ADR-0036 just made broker-owned — retry
counting, backoff, a parking lot — as a second state machine, plus a second poller on a
scale-to-zero database. The variant earns its keep when a consumer does transactional database
work per message or the broker cannot retry properly; neither is true here.

### B. Status quo — natural idempotency only

Rejected: no durable trace of processing, so the duplicate-e-mail window spans the entire gap
between "sent" and "acked" (including whole crash-restart cycles), and every future message
type must re-earn idempotency from its business rules. ADR-0035 §6's promise would stay
unkept.

### C. Claim-before-send (insert the marker first)

Rejected for the asymmetry in Decision 2: it converts duplicate-e-mail risk into lost-e-mail
risk, and a silent nothing is the worse failure for a confirmation e-mail.

### D. Circuit breaker in the consumer (Polly)

Rejected as redundant: with prefetch 1 and the in-process backoff pause (ADR-0036 Decision 4),
a failing dependency already halts *all* consumption — the "open circuit" exists structurally,
with zero additional configuration to tune or get wrong.

### E. Idempotency key at the e-mail provider

The only mechanism that could make the send itself exactly-once — and inapplicable over SMTP.
Not an alternative to the inbox but its complement; recorded in Consequences as the designated
follow-up if the transport ever becomes an HTTP API.

## Implementation Notes

- `InboxMessage` (Persistence, `Inbox/` beside `Outbox/`) — `Create` factory + guards,
  private EF constructor, mirroring `OutboxMessage`; configuration via Fluent API with
  `nameof()` column names; purely additive migration (trivially N-1 safe under ADR-0023).
- `EmailConfirmationConsumer.OnDeliveredAsync` — the only consumer change: id parse (poison on
  absence), inbox lookup, post-success insert; `AuthDbContext` resolved from the existing
  per-delivery scope like the relay does — no new abstraction. A primary-key violation on the
  insert is treated as "already processed" (ack): a concurrent duplicate lost the race, which
  means the work is done.
- Tests, matching the branch's discipline: unit (`InboxMessage` factory guards) + integration
  against the real broker (duplicate publish of one message id → exactly one e-mail via the
  spy sender, one inbox row, empty queue; processor failure → no inbox row, redelivery really
  retries; missing message id → parks in the DLQ, zero e-mails).

## References

- ADR-0035 — outbox relay; §6 at-least-once semantics and the message-id deduplication
  contract this ADR fulfils.
- ADR-0036 — broker-enforced dead-lettering; delivery limit, backoff ladder, replay rules.
- RabbitMQ docs: Quorum Queues (`x-delivery-limit`, `x-delivery-count`).
- The idempotent-consumer / inbox pattern as described for NServiceBus ("Outbox") and
  MassTransit ("transactional inbox") — this is the dedup-guard flavor, chosen over the
  store-and-forward flavor per Alternative A.
