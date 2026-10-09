# ADR-0065: An Account Erasure Reaches the Outbox and the Translation System

**Status:** Accepted
**Date:** 2026-10-09
**Decision-makers:** Solo maintainer (ticket #1071)
**Related:** AuthSystem.API (`Services/Gdpr/AccountErasureService.cs`, `ErasedAccountReconciler.cs`,
`AccountDeletionFinalizer.cs`, `Outbox/OutboxMessageRouting.cs`,
`Services/Emails/EmailChangeCompletedProcessor.cs`, `Settings/GdprSettingsValidator.cs`);
AuthSystem.Infrastructure (`Messaging/RabbitMqMessagePublisher.cs`, `RabbitMqTopologyDeclaration.cs`);
SharedKernel (`IntegrationEvents/AccountErased.cs`, `AccountEvents.cs`); TranslationSystem.API
(`Messaging/AccountErasedConsumer.cs`, `AccountEventsTopology.cs`,
`Features/Translators/EraseTranslatorProfile.cs`); TranslationSystem.Domain (`Translator.Erase`);
amends ADR-0004 §1, ADR-0031 and ADR-0037 §6; builds on ADR-0035, ADR-0036, ADR-0038, ADR-0048;
follow-ups #1085, #1086, #1087

## Context

The privacy policy (sections 06 and 07) promises that a deleted account's personal data is
anonymized for good once the 14-day grace period is over, and that its translations stay only with
an id that points at nobody. Two stores broke that promise.

**The outbox.** An e-mail change writes `EmailChangeRequested` and `EmailChangeCompleted` to the
outbox, and both payloads carry the old and the new address (ADR-0048). ADR-0037 §6 keeps every sent
row forever. It was written when every payload held only the account id. So after the erasure both
addresses stayed in `authsystem.OutboxMessages` with no end date.

**The translator profile.** ADR-0031 kept the erasure inside the AuthSystem because "the
TranslationSystem stores only opaque `IdentityId` attribution references". That was never true. Since
ADR-0004 the TMS keeps a `Translator` profile with the person's display name and e-mail, copied from
the token claims, and the editor shows the name as the author of their translations. Nothing ever
removed it.

The ticket asked for every store of this class, not only the first one. The review of the plan
checked the rest:

| Store | After the erasure |
|---|---|
| `Users` row | anonymized, the undo target cleared (ADR-0031, #684) |
| `SignInSessions` | deleted (ADR-0062) |
| `UserRoles` / `UserClaims` / `UserLogins` | removed; claims and logins are never written anyway |
| `UserTokens` | never written: the token providers are stateless |
| `InboxMessages` | the message id and a time only |
| OpenIddict tokens and authorizations | revoked by the erasure; the daily prune deletes a revoked row once it is 14 days old, and every token of the account is at least that old by then (§7) |
| `OutboxMessages` | **kept both addresses** (this ADR, §5) |
| TMS `Translators` | **kept the name and the e-mail** (this ADR, §2–§4) |
| TMS translations, game versions, the translation file | no names |
| Frontend | no server-side store beyond five-minute in-memory caches |
| The broker's `emails.send.dlq` | can keep a parked e-mail change message forever (#1085, needs an owner decision on a time limit) |
| Logs | 30 days, as the policy says |

## Decision

### 1. The AuthSystem tells the TMS with an outbox message, in the anonymizing save

`AccountErasureService` adds `AccountErased(IdentityUserId)` to the outbox before the save that
anonymizes the account, so the two commit together. It has to be the same save: the finalizer picks
its work by the anonymized address, so no run comes back to an account after that save, and a message
written later and lost would leave the name in the TMS for good. The enqueue sits outside the
erasure's `try`, so a contract with no route still fails loudly (ADR-0038). A save that failed but
landed is treated like a save that answered, so it also wakes the relay.

The message and its broker address live in `SharedKernel/IntegrationEvents`, the one place both
contexts already share (`AnonymizationConstants` sits next to it). The AMQP `type` on the wire is the
record's name.

### 2. A topic exchange of its own: `lotro.accounts`, routing key `account.erased`

The outbox routing table now maps a type to an exchange **and** a routing key, and
`IMessagePublisher.PublishAsync` takes the exchange. E-mail stays on `lotro.emails`. An account event
must never reach the e-mail queue, and e-mail work must never reach a consumer of account events.

The AuthSystem declares `lotro.accounts` with the rest of its topology, so its publish never fails
on a missing exchange. It does not declare the TMS queue. It publishes with `mandatory` and publisher
confirmations, so an event published before the TMS queue exists comes back as an exception, and the
outbox row stays for the next try. It is never dropped.

Rows like that can fail for a long time, for example on a first rollout before the TMS has declared
its queue, or after a rollback. So the publisher reports a returned message as
`MessageNotRoutedException`, and the relay skips such a row for the rest of its pass and takes the next
batch from behind it. Before this, 100 such rows at the head of the outbox filled every batch and held
back every newer e-mail. Any other publish failure still ends the pass at once, because a broker that
is down refuses every row the same way. This refines the batched drain of ADR-0035.

### 3. The TMS owns its queue and only checks the exchange

`AccountEventsTopology` declares `tms.account-erased` (a quorum queue, like `emails.send`), its
dead-letter exchange and its parking queue `tms.account-erased.dlq`, and binds the queue to
`account.erased`. It declares `lotro.accounts` **passively**. So the two contexts can never declare
the exchange with different arguments (a mismatch would fail the AuthSystem's whole channel with
`PRECONDITION_FAILED` and stop all e-mail), and a narrower broker user for the TMS later needs no
right on it (#1086). On a fresh broker the TMS consumer retries its connection until the AuthSystem
has started once.

### 4. The TMS consumer: idempotent, no inbox, and a database outage never parks a message

`AccountErasedConsumer` mirrors `EmailDispatchConsumer` (push, manual ack after the save, a broker
outage never stops the API) and hands each event to `EraseTranslatorProfile`, which calls
`Translator.Erase()`: the display name becomes **„Usunięte konto”** and the e-mail is cleared. The
profile and its id stay, so the translations stay credited to a profile that no longer says whose it
was. An account that never opened the TMS has no profile, and that counts as done.

- **No inbox.** Erasing twice gives the same profile as erasing once, so a duplicate is harmless.
  ADR-0037 §5 (one inbox, one consumer) is untouched.
- **What can never work is parked at once.** An unknown type, an unreadable payload or a command the
  handler refuses goes to the parking queue with `basic.reject` without requeue, as in ADR-0036.
- **An unavailable database is waited out with `basic.nack`.** The e-mail consumer gives up after
  five retries, about 30 minutes, because an e-mail that late is worth little. An erasure that gives
  up is worse: nothing would ever send it again. RabbitMQ 4.3 does not count a nack against the
  delivery limit, so the consumer pauses (30 seconds growing to 15 minutes, each under the broker's
  30-minute consumer timeout) and returns the message, for as long as the outage lasts. Once the
  pauses stop growing it logs at Error. "Unavailable" means any database error (a `DbException`), a
  timeout, a socket error or EF's `RetryLimitExceededException`, anywhere in the exception chain. Not
  only the errors Npgsql calls transient: the erasure reads one profile by its id and writes fixed
  values, so a rotated password, a lost grant or a missing table is the environment, never the
  message, and a person fixing it lets the waiting erasures through.
- **Any other exception is retried with `basic.reject`, which counts.** That is a bug, not an
  outage, and it fails the same way every time, so it ends in the parking queue at the delivery
  limit instead of looping and blocking the erasures behind it.
- **It attaches again when the broker drops it.** Automatic recovery brings back a lost connection,
  but not a channel the broker closed or a subscription it cancelled, for example when the queue is
  deleted to change its arguments. The consumer checks every 30 seconds and attaches again after two
  checks in a row find it detached. A cancellation the client reports for its own timeout is retried
  like any other failed attach; only the host's own stop ends the consumer.

The owner chose the outbox over an HTTP call and the text „Usunięte konto” over a per-account marker
at the #1071 gate.

### 5. Every finalizer run reconciles the outbox with the erased accounts

`ErasedAccountReconciler` runs at the end of every finalizer run, over **all** erased accounts. Its two
steps run and fail on their own, so a scrub that keeps failing never keeps the TMS from hearing about
an earlier erasure. A shutdown skips both: the next run does them, and there is nothing to finish.

- **It cuts every sent message of an erased account down to `{"IdentityUserId": …}`.** The row keeps
  its type, its times and the account id. One SQL statement does it. Unsent rows are left alone: the
  consumer would read a cut payload as poison, and deleting a row could race the relay. The next run
  cuts a row once the relay has sent it. Duplicate detection (ADR-0037) reads `InboxMessages`, never
  the outbox, so a cut row changes nothing for the consumer. A `LIKE` on the erased ids keeps the
  JSON parsing to the few rows that name an erased account. A row that is not JSON is skipped
  (`pg_input_is_valid` before the cast), so one bad row cannot stop every later run. Table and column
  names come from the EF model, so a renamed column fails a test, not a production run.
- **It writes an `AccountErased` for every erased account that has none.** That covers the accounts
  erased before this ADR. It is a check in every run and not a one-off migration, so the rows appear
  only once this release runs, and the migration history carries no data. A release rolled back past
  this ADR still meets them: its relay marks them failed and keeps them until a release that routes
  them is back (runbook, "Rolling back past ADR-0065"). It refuses to run on a context that still
  tracks changes, so a message a failed erasure left behind is never saved with its own.

`ErasedAccounts.Rule` is the one definition of an erased account, used by the reconciler and by
§6: the `anon-` address on the anonymization domain and no password. Neither half is proof alone. A
cancelled deletion and an undone e-mail change clear a live account's password, and the registration
form accepts any address. But no mail reaches the anonymization domain, so an account with that
address can never confirm it, and only a confirmed account reaches those two flows. The deletion
date is not part of the rule: the immediate deletion before two-phase deletion (#460) never wrote
one. `ErasedAccounts.Includes` is the same check for an account in memory, ordinal like the SQL `LIKE`.

### 6. A late e-mail change notice is not sent to an erased account

`EmailChangeCompletedProcessor` acknowledges without sending when the account is erased. Its message
could only arrive that late after a relay outage of 14 days or more, but then it would mail the
erased person's addresses again.

### 7. The grace period outlives an access token

`GdprSettingsValidator` now refuses a `DeletionGracePeriod` that is not longer than
`OpenIddict:AccessTokenLifetimeMinutes` plus the five minutes of clock skew the TMS's JWT validation
allows. Signing in stops when the deletion is scheduled, but an access token issued just before still
works on the TMS until then, and the TMS copies the token's name and address into the profile. A
longer grace period means no token is left to write them back after the erasure. QA can still shorten
the period to watch a real erasure, as ADR-0031 describes.

With the default 14 days, every token of the account is also older than the OpenIddict prune's 14-day
retention at the erasure, so the encrypted copy of the name and address in a stored refresh token goes
within a day. A shorter QA grace period leaves those encrypted rows until the prune reaches them.

## Amendments to earlier decisions

- **ADR-0031** — its "no cross-context call" deviation is withdrawn. The TMS holds personal data, and
  the erasure now reaches it through §1–§4. The eventual-consistency risk ADR-0031 wanted to avoid is
  bounded by §4: an outage delays the TMS erasure, it never loses it.
- **ADR-0037 §6** — sent rows are still kept, but not their personal data: §5 cuts the payload of an
  erased account's rows. The column comment in `OutboxMessageConfiguration` says the same: a row
  records exactly what went on the wire, until its account is erased.
- **ADR-0004 §1** — "archival/anonymization" is no longer out of scope for the `Translator`: it is
  erased by §4.

## Consequences

**Positive**

- No database table holds an erased account's addresses or name. An integration test scans every
  text column of the auth schema for the person's old address, new address and username after an
  erasure, so a table added later that keeps them fails the build.
- The privacy policy's sections 06 and 07 are true for both databases. Its text needs no change.

**Negative**

- The TMS now talks to the broker, with the same broker user as the AuthSystem. A broken-into TMS
  could publish to the e-mail exchange. It very likely holds the sign-in server's database user
  already, so this adds little today, but both are tracked in #1086.
- A parked e-mail change message in `emails.send.dlq` still keeps both addresses until a person
  removes it (#1085).
- A TMS consumer that cannot erase shows only in the logs: one Error line every 15 minutes once the
  pauses stop growing. No alert or health check watches it or the parking queues yet (#1087).
- Each finalizer run reads every sent outbox row once. That is a few thousand small rows at this
  project's size, read once a day by a run that wakes the database anyway.
- Sent payloads of erased accounts are no longer byte-for-byte what went on the wire.

## Alternatives considered

- **An HTTP call from the erasure to a TMS endpoint, before the anonymizing save.** It would make the
  erasure wait on the TMS and need a client-credentials flow from the sign-in server to itself. The
  outbox gives the same "never lost" result with the parts that already exist. Rejected at the gate.
- **Deleting sent rows of erased accounts instead of cutting them.** It works too, since the inbox
  does not depend on them, but it throws away the type and times a person needs to diagnose a past
  e-mail problem.
- **A one-off EF migration as the backfill.** See §5: unsafe for a rolled-back release.
- **Keeping ADR-0036's reject-and-park for TMS database failures.** About 30 minutes of outage would
  have parked an erasure with no consumer and no alert.

## Verification

- Auth integration: `ErasedAccountPersonalDataTests` (the text-column scan; one `AccountErased` per
  erasure, sent to `lotro.accounts`; sent messages cut, unsent left alone, live accounts untouched,
  accounts erased before the fix cleaned and announced once, with or without a deletion date, a
  non-JSON row skipped, a second run changes nothing). The scan fails with `OutboxMessages.Payload`
  when the reconciliation is switched off. `AccountDeletionFinalizerTests` count the `AccountErased`
  rows on every failure path of the erasure (0 after a cancel or a failed save, 1 after a retry,
  another run's erasure or a lost commit answer), and pin that the reconciliation never saves the
  message a failed erasure left tracked.
- TMS integration: `EraseTranslatorProfileTests` (the database and the editor's author name),
  `Messaging/AccountErasedConsumerTests` (an event published like the relay erases the profile; a
  database that fails more times than the delivery limit still ends in an erased profile and an empty
  parking queue; poison is parked), `Messaging/AccountEventsTopologyTests` (against RabbitMQ 4.3.4: a
  nack never parks, a reject parks at the limit).
- Unit: `Translator.Erase`, the routing table and the `IdentityUserId` key of every routed contract,
  the consumer's pauses and payload checks, the TMS broker settings, the late e-mail change notice,
  the grace-period rule.
