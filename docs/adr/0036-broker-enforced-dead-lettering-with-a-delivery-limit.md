# ADR-0036: Broker-enforced dead-lettering with a quorum-queue delivery limit

**Status:** Accepted (amended 2026-10-11 by #1085 — a parked e-mail message expires after 7 days;
see §6 in the amendment below)
**Date:** 2026-08-03
**Decision-makers:** Solo maintainer (amendment #1085: owner decision 2026-10-11)
**Related:** ADR-0035 (signal-driven outbox relay), ADR-0034 (Hetzner one-box compose deploy),
`RabbitMqTopology`, `EmailConfirmationConsumer`, `docs/adr/0035` §6 (at-least-once semantics),
ADR-0031 (the 14-day deletion grace period), ADR-0065 (what an account erasure reaches),
`scripts/hetzner/deploy.sh`

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
DLQ; it sets no explicit delivery limit and no DLX of its own — it is terminal by design. (Since
§6, #1085: a parked message expires after 7 days.) It
still inherits the quorum **default** delivery limit (20), which constrains how replay may ever
consume it (Decision 5).

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
message type is the outbox-independent resend endpoint (ADR-0035). One hard rule binds any
future re-drive tooling: **ack-and-republish, never reject-requeue** — the DLQ inherits the
quorum default delivery limit (20) and has no DLX of its own, so a reject-requeue loop against
it would silently drop the parked message after 20 attempts.

## Consequences

### Positive

- **No silent loss anywhere:** poison parks instead of vanishing; exhausted retries park
  instead of looping; the park hop itself is at-least-once.
- **Every failure loop is bounded** — including consumer crash loops — by one broker-enforced
  counter shared with the backoff ladder (`RabbitMqTopology.EmailDeliveryLimit`).
- ~33 min of SMTP outage rides out on backoff alone, no human needed; beyond that the message
  waits intact in the DLQ (for at most 7 days since §6, #1085).
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

## Amendment (2026-10-11, #1085): a parked e-mail message expires after 7 days

Decision 2 kept every parked message until a person replayed or removed it, with no end date. An
e-mail change message (`EmailChangeRequested`, `EmailChangeCompleted`) holds the person's old and new
address. An account erasure cannot reach a message in a queue, so when that person later deleted the
account, both addresses could stay on the broker for good. The other e-mail messages hold only the
account id. The review of #1071 found this.

### 6. `emails.send.dlq` keeps a message for 7 days, through a broker operator policy

**The rule (owner decision, 2026-10-11).** A parked e-mail message expires 7 days after it parks. A
week-old account e-mail is no use to anyone. An account erasure runs 14 days after the deletion request
at the earliest (ADR-0031), so a message parked around the time of the request is gone well before the
account is. A message nobody replays within the 7 days is lost for good. That is accepted, and it gives
the manual replay of Decision 5 a deadline.

**How it is applied.**

- A RabbitMQ policy, not a queue argument: name `emails-send-dlq-ttl`, pattern `^emails\.send\.dlq$`,
  definition `{"message-ttl":604800000}`, applied to queues. A live queue's arguments cannot change
  (Context), and a policy changes none, so `RabbitMqTopologyDeclaration` stays as it is.
- An **operator** policy (`set_operator_policy`), not a user policy. A queue follows only one user
  policy, the matching one with the highest priority, so a user policy added later in the management
  UI would switch the expiry off. An operator policy applies next to whatever user policy matches, and
  where both set `message-ttl` the lower one wins. So no later policy can lengthen or remove the limit,
  and nobody has to remember a rule about it (seen on `rabbitmq:4.3.4`: a user policy with priority 10
  and a 10-minute TTL left the operator's limit in force).
- `scripts/hetzner/deploy.sh` applies it on every deploy, on both boxes, right after `up -d`, which is
  what creates a new broker. `set_operator_policy` replaces a policy of the same name, so a repeat
  changes nothing. Nobody applies it by hand. The policy is stored on the broker's volume, so a
  rollback or a recreated container keeps it.
- The CLI runs as the `rabbitmq` user. On a new volume a root `rabbitmqctl` that runs before the
  server has written its Erlang cookie writes the cookie first, owned by root, and the server then
  stops at boot because it cannot read it (seen on `rabbitmq:4.3.4`).
- The step tries again for up to 180 s, the broker healthcheck's own budget, each try under a 20 s
  limit, with stdin closed: `timeout` runs the call outside the terminal's process group, and in a
  hand-run deploy a client that reads the terminal there is stopped. A try that runs out of time ends
  the step, because a starting broker refuses at once and only a stuck one holds the call. The whole
  step is bounded at about three and a half minutes.
- A broker that does not take the policy gets a warning in the deploy log, not a failed deploy: a
  broker outage must never block an app deploy (`compose.hetzner.yaml`). A broker that had the policy
  keeps it. Only a new broker is left without it (see fact 2 below for why that gap matters).
- Every way this project brings up a broker goes through `deploy.sh`: CD, a hand-run deploy, and the
  runbook's bring-up and disaster-recovery steps. A broker started with a plain `docker compose up -d`
  has no policy until `deploy.sh` runs.
- Only `emails.send.dlq`. **Never `tms.account-erased.dlq`:** a message parked there is erasure work
  (ADR-0065), and it waits for a person however long that takes. Never `emails.send` either: an e-mail
  waiting out an outage there must not be dropped.

**Two quorum-queue facts this depends on** (both seen on `rabbitmq:4.3.4`, 2026-10-11):

1. A message gets its expiry when it enters the queue. A policy added later does not reach a message
   that is already there.
2. A quorum queue drops expired messages only from its head. One message with no expiry at the head
   holds back every message behind it, expired or not. With fact 1, this means a message that parks
   while the policy is missing never expires, and it stops the expiry of everything after it. So the
   policy must be in place before anything parks. Both boxes' parking lots were empty when checked
   on 2026-10-11, before the first deploy with the policy. The runbook says what to do if one is not.

**Rejected: a definitions file loaded at boot** (`load_definitions`), so the broker would apply the
policy itself on every start. With a definitions file configured, RabbitMQ does not create the default
vhost and user from `RABBITMQ_DEFAULT_USER` / `RABBITMQ_DEFAULT_PASS`, and a new broker stops at boot
("Please create virtual host "/" prior to importing definitions"). Adding the user to the file would put
the broker password in a committed file.

**Not done: a default operator policy in `rabbitmq.conf`** (`default_policies.operator.*`, which the
4.3.4 broker accepts). It would give a new broker the limit when its vhost is created, before anything
can park, and so close the gap a failed deploy step leaves. But it acts only on a new vhost, so the
deploy step stays for the boxes that exist, and the rule would then live in two places, with a new
mounted file for the deploy to ship and roll back. The gap it closes needs a new broker and a deploy
step that failed, and the warning already covers that. Worth it only if that ever happens.

**Rejected: `x-message-ttl` in the queue's arguments.** Changing it on a live box means deleting the
queue (Context). The policy does the same job without that, which is why the owner chose it.

**Rejected: failing the deploy when the policy does not land.** It would roll good apps back over a
broker problem, against the rule above. The cost of the choice: a new broker that only comes up after
the deploy has no policy until the next deploy, and a message that parks in that window never expires
and holds back every message behind it (facts 1 and 2). The warning says so and asks for a new deploy
and a look at the parking lot.

**Not in the dev and production-parity stacks** (`compose.yaml`, `compose.prod.yaml`). They hold no
real person's data, and `deploy.sh` does not run there.

**Tests.** `scripts/tests/hetzner-deploy.tests.sh` pins the exact call, its place after `up -d`, its
time limit and closed stdin, the retries (a starting broker, a restarted one), the stop after a
timed-out try, the warning, and that the pattern matches `RabbitMqTopology.EmailDeadLetterQueue` and no
other queue the apps declare. `DeadLetterExpiryTests` reads the name, pattern and definition from
`deploy.sh`, cuts only the message-ttl, and shows on the pinned broker image that this policy removes a
parked message with no consumer.

## References

- RabbitMQ docs: Quorum Queues — delivery limit, `x-delivery-count`/`acquired-count` increment
  table (the ≥ 4.3 reject-vs-nack rule); Dead Lettering — `at-least-once` strategy and its
  `reject-publish` prerequisite.
- ADR-0035 — outbox relay; at-least-once + `message-id` deduplication contract.
- ADR-0034 — one-box compose topology (the ops-surface constraint).
