# ADR-0038: One dispatch pipeline for every transactional e-mail

**Status:** Accepted
**Date:** 2026-08-04
**Decision-makers:** Solo maintainer
**Related:** AuthSystem messaging, epic #578 (MSG-00), ticket #579 (MSG-01), ADR-0001 (no
mediator), ADR-0035 (signal-driven outbox relay), ADR-0036 (broker-enforced dead-lettering),
ADR-0037 (consumer-side inbox dedup)

## Context

Since #575/#577 the outbox → relay → RabbitMQ → consumer → inbox pipeline exists and is proven
end-to-end, but it carries exactly one message type: `EmailConfirmationRequested`, written only
by `RegisterUser`. Every other transactional e-mail still goes out synchronously, in-request,
straight to SMTP. All of them live in AuthSystem (TMS, Frontend and the patcher send no e-mail).

Code facts that constrain the generalization (verified against HEAD; where epic #578's table
disagrees, these win):

- **Password reset** (`ForgotPassword.Handler` + its SSR twin
  `Pages/Account/ForgotPassword.cshtml.cs` — two call sites, one flow): mints the reset token
  in-request, sends, logs a failure and returns success regardless (anti-enumeration). The send
  only happens for existing accounts, so response time leaks account existence despite the
  dummy-hash equalization.
- **Deletion scheduled** (`DeleteAccount.Handler`): mints the cancel token via
  `GenerateUserTokenAsync` (bound to the security stamp, so it must be minted *after* the stamp
  rotation) and **compensates on send failure** — `TryUnwindScheduleAsync` reverts the schedule
  and the request fails, because a locked account without its emailed cancel link would be
  unrecoverable by the owner.
- **Deletion cancelled** (`CancelAccountDeletion.Handler`): the e-mail is a courtesy notice and
  **carries no token at all** — the forced password-reset token travels in the API response
  (`CancelledDeletion.PasswordResetToken`), not in the e-mail (epic #578's table overstates
  this). Send failure is logged and ignored.
- **Resend confirmation** (`ResendEmailConfirmation.Handler`): deliberately outbox-independent —
  ADR-0035 priced its 6 h orphan tail on exactly this fallback existing.
- The consumer's ack/reject contract is already type-agnostic: poison parking, unusable-id
  parking, the redelivery backoff ladder, the broker delivery limit (ADR-0036) and inbox dedup
  (ADR-0037) never look at the payload type. Only two things are type-specific: `TryDeserialize`
  hard-targets `EmailConfirmationRequested`, and the processor is resolved concretely.
- **No type discriminator travels on the wire today.** `RabbitMqMessagePublisher` stamps
  message id, content type, encoding, persistence and timestamp — not the outbox row's `Type`.
  The consumer can only deserialize blind because one type exists.
- `OutboxMessageRouting` already keeps `Type` (payload contract name) and routing key as
  separate concepts, precisely so a contract rename cannot silently unroute a live queue.
- ADR-0035 §2 made after-commit `OutboxSignal.Notify()` a per-writer convention and set an
  escalation trigger: "if writers multiply (≥3), promote to an interceptor pair via a follow-up
  ADR". This migration takes the outbox from one writing flow to four (five call sites,
  counting the `ForgotPassword` SSR twin) — the trigger fires and must be answered here.
- Owner decisions (2026-08-04, extracted per ticket #579, not invented): resend stays
  synchronous; `DeleteAccount` drops its compensation in favor of pipeline-owned delivery.

## Decision

### 1. One consumer, per-type processors selected by the payload contract name

`EmailConfirmationConsumer` generalizes into the single e-mail dispatch consumer. Each message
type gets an `IEmailMessageProcessor` implementation that owns its payload deserialization,
token minting and send; the consumer selects it from an **explicitly DI-registered** registry —
no mediator, no assembly scanning (ADR-0001: wiring stays a compile-visible inventory). The
publisher starts stamping the outbox row's `Type` into the AMQP `type` basic property; the
consumer selects by that property, never by routing key (the type says what the payload *is*,
the routing key says which bindings *receive* it — `OutboxMessageRouting`'s separation, now
end-to-end). A delivery with a missing or unregistered type is poison: no redelivery can fix it,
so it parks in the DLQ immediately, exactly like an unusable message id.

The ack/reject contract, the redelivery ladder and the delivery limit stay type-agnostic and
unchanged. The inbox wrapper (`EmailConfirmationDeliveryProcessor`) generalizes with the
consumer; the inbox table stays one undiscriminated table (ADR-0037 §5 holds — message ids are
outbox row ids, unique across types). At-least-once delivery is unchanged, so **every new
processor must re-earn idempotency** before it ships, as `EmailConfirmationRequestProcessor`'s
remarks already demand.

### 2. Payloads carry identifiers only; tokens and state are minted at delivery

Every payload contract carries the user id and nothing secret or derivable. Identity tokens
(confirmation, password reset, deletion cancel) are generated inside the processor at send time
— the pattern `EmailConfirmationRequested` set. A live reset token must never persist in an
outbox row, a broker frame, or a DLQ-parked message; minting at delivery also keeps tokens valid
against the *current* security stamp and makes "the countdown starts now" true in every e-mail
no matter how long the message waited. The same rule covers derivable state: the
deletion-scheduled processor recomputes `finalizesAt` from `ApplicationUser.DeletionScheduledAt`
plus `GdprSettings.DeletionGracePeriod` instead of snapshotting it into the payload. The
deletion-cancelled e-mail needs no token at all (see Context) — its payload is the user id alone.

### 3. Topology unchanged: one queue, three new routing keys

`lotro.emails` + `emails.send` + the existing DLX/DLQ stay exactly as declared. New types bind
by adding `email.password-reset`, `email.deletion-scheduled` and `email.deletion-cancelled` to
`RabbitMqTopology` and `OutboxMessageRouting` — the `email.#` binding already routes them with
zero binding changes (that was the point of `#`). No queue-argument changes: arguments are
immutable (`PRECONDITION_FAILED` — the ADR-0036 runbook trap), and one queue keeps prefetch-1
serial sending, one delivery limit and one parking lot. If a type ever needs its own consumer,
the topic exchange lets it bind its own subset then — not now.

### 4. Resend confirmation stays synchronous — the documented escape hatch

Owner decision. `ResendEmailConfirmation` keeps its direct `IEmailService` send: it is the
user-driven fallback ADR-0035's 6 h orphan tail is priced on, and it must keep working when the
pipeline itself is the thing that is stuck (relay dead, broker down, consumer wedged). It is the
**only** intentional direct-SMTP call on a request path; rate limiting already bounds its abuse.
MSG-05 (#583) reduces to documenting this in code so a future cleanup doesn't "helpfully"
migrate it and silently invalidate ADR-0035's pricing.

### 5. Request success means "outbox row committed" — delivery failure is the pipeline's

After migration no user-facing request observes SMTP. Per flow:

- **`RegisterUser`** — already the pattern (#575); unchanged.
- **`ForgotPassword`** (both call sites) — observable behavior unchanged (always success,
  anti-enumeration). Bonus: the send leaves the request path, closing the response-time leak.
- **`CancelAccountDeletion`** — observable behavior unchanged (it already ignored send
  failures); the courtesy e-mail merely becomes reliable instead of best-effort.
- **`DeleteAccount`** — **behavior change, owner-accepted.** The unwind compensation is deleted.
  The outbox row must commit **atomically with the schedule mutation** (one transaction), so
  "scheduled but no e-mail ever recorded" cannot exist; a database failure still fails the whole
  request before anything is scheduled. After commit, the account locks immediately and the
  cancel e-mail is *eventually delivered*: the redelivery ladder rides out SMTP outages, and a
  message that exhausts the delivery limit parks in the DLQ for manual replay (ADR-0036 §5).
  The failure mode "locked account, no cancel link" changes from *prevented by unwinding* to
  *healed by guaranteed delivery or an operator replay*.

### 6. The after-commit `Notify()` stays explicit — ADR-0035 §2's trigger answered

Writing flows grow from one to four, meeting ADR-0035's ≥3 escalation trigger. Ruling: keep the explicit
per-writer call, do not build the interceptor pair. The new writers commit through a single
`SaveChangesAsync` (none reproduces `RegisterUser`'s explicit-transaction trap), a forgotten
call is a soft failure bounded by the 6 h sweep, and an interceptor pair is machinery a
four-writer inventory doesn't earn (YAGNI). Revisit only if a forgotten notify actually bites.

### 7. Future senders publish to the broker

No bounded context outside AuthSystem sends e-mail today. Any future TMS notification (post-MVP
TP-00 ideas) publishes a new contract to `lotro.emails` instead of growing its own SMTP path:
new payload record + processor + routing-key registration. `IEmailService`/SMTP remains a
delivery detail behind the consumer (plus the decision-4 exception).

## Consequences

### Positive

- One delivery semantic for every e-mail: retry ladder, DLQ parking, inbox dedup, replay — no
  per-flow ad-hoc error handling.
- An SMTP outage stops failing user-facing requests (`DeleteAccount` today) and stops silently
  dropping courtesy mail (`CancelAccountDeletion` today).
- `ForgotPassword` stops leaking account existence through response timing.
- No live token can leak via an outbox row, broker frame or parked DLQ message; tokens are
  always fresh against the current security stamp.
- `DeleteAccount` can no longer end in the unmonitored worst case of its compensation
  (`LogScheduleUnwindFailed`: locked, scheduled, no e-mail, manual intervention) — the pipeline
  owns exactly that state.

### Negative / Accepted Trade-offs

- **`DeleteAccount` loses synchronous certainty.** The user can sit locked while the cancel
  e-mail rides the retry ladder (~30 min ceiling before DLQ). MTTR of a parked message is
  operator response time, and DLQ depth is only visible in logs and the management UI — a
  monitoring gap accepted at current scale.
- The decision-4 exception must be actively defended: one deliberately non-migrated sender is
  the kind of "inconsistency" reviews love to erase. Mitigated by MSG-05's in-code documentation.
- E-mail content must be derivable at delivery time; anything request-scoped that a future
  e-mail wants to show has to be re-derivable from the aggregate or it forces a payload-design
  discussion here.
- A new e-mail costs payload record + processor + routing key + registry line where it used to
  cost one sender call — plus a per-processor idempotency argument. Accepted: that ceremony IS
  the delivery guarantee.
- Four explicit `Notify()` call sites instead of one interceptor (decision 6) — a forgotten one
  costs up to 6 h of latency until the sweep.

## Alternatives Considered

### A. Status quo — only the confirmation e-mail through the broker

Two delivery semantics forever; SMTP outages keep failing `DeleteAccount` and dropping
cancellation notices; the timing leak stays. Rejected.

### B. One queue per message type

Per-type delivery limits and parking lots — and per-type immutable queue arguments, each a
standing commitment (ADR-0036), multiplying the ops surface for a message volume near zero. The
topic exchange already allows a future consumer to bind its own subset without republishing.
Rejected.

### C. Select the processor by routing key instead of an AMQP type property

Saves one property stamp, but fuses "what the payload is" to "which bindings receive it" — the
exact drift `OutboxMessageRouting` exists to prevent; a key rename would silently mis-select
processors on in-flight messages. Rejected.

### D. Assembly-scanned or convention-based processor registry

Runtime-discovered wiring is the complaint that produced ADR-0001; the explicit registration
list doubles as the message-type inventory. Rejected.

### E. Migrate resend too (uniformity over the escape hatch)

Leaves the pipeline with no bypass when the pipeline itself is stuck, and re-opens ADR-0035's
6 h tail pricing — the tail is only acceptable *because* resend bypasses it; tightening the
sweep instead would buy back the Neon CU-h burn that ADR designed away. Rejected by owner.

### F. Keep `DeleteAccount` synchronous like resend

Preserves the unwind, but keeps a user-facing request hostage to SMTP and adds a second
permanent exception with a weaker reason than resend's (resend is the *pipeline's* fallback;
this would merely distrust the pipeline). The state the unwind protected against is eliminated
more strongly by the atomic outbox commit. Rejected by owner.

## Implementation Notes

Recorded here, implemented by MSG-02..05 — this ADR changes no code.

- **MSG-02 (#580), no behavior change:** `IEmailMessageProcessor` + explicit registry; consumer
  generalization (deserialization moves behind the processor seam); publisher stamps the AMQP
  `type` property; unknown-type poison path; `EmailConfirmationDeliveryProcessor` generalizes;
  `EmailConfirmationRequestProcessor` becomes the first registry entry.
- **MSG-03 (#581):** `PasswordResetRequested(Guid)` + processor + `email.password-reset`;
  `ForgotPassword.Handler` and `Pages/Account/ForgotPassword.cshtml.cs` write outbox rows.
- **MSG-04 (#582):** `AccountDeletionScheduled(Guid)` + `AccountDeletionCancelled(Guid)` +
  processors + routing keys; `DeleteAccount` swaps send+unwind for an atomic outbox write;
  `CancelAccountDeletion` swaps its fire-and-forget send; `finalizesAt` recomputed at delivery.
- **MSG-05 (#583):** in-code documentation of the resend exception (decision 4), pointing here.
- Touched types: `EmailConfirmationConsumer`, `RabbitMqMessagePublisher`, `RabbitMqTopology`,
  `OutboxMessageRouting`, `EmailConfirmationDeliveryProcessor`, the three e-mail senders behind
  the processors, the four feature slices above.
- Every migrated writer follows ADR-0035 §2: `OutboxSignal.Notify()` after its commit.

## References

- Epic #578 (MSG-00) — inventory and motivation; ticket #579 (MSG-01) — the six decision points.
- ADR-0035 — outbox relay, orphan-tail pricing, the `Notify()` convention this ADR's decision 6
  answers.
- ADR-0036 — dead-lettering, delivery limit, queue-argument immutability, manual replay.
- ADR-0037 — inbox dedup; its §5 single-table constraint is unaffected (decision 1).
- ADR-0001 — explicit wiring; why the processor registry is hand-registered.
- ADR-0031 — the GDPR deletion flow whose compensation decision 5 retires.
- PR #575 — the single-type pipeline this ADR generalizes; #577 — the edge-test suite proving it.
