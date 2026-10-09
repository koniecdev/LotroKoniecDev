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
follow-ups #1085, #1086

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
- **Poison is rejected at once.** An unknown type or an unreadable payload goes to the parking queue
  with `basic.reject`, as in ADR-0036.
- **A database failure is returned with `basic.nack`.** The e-mail consumer gives up after five
  retries, about 30 minutes, because an e-mail that late is worth little. An erasure that gives up is
  worse: nothing would ever send it again. RabbitMQ 4.3 does not count a nack against the delivery
  limit, so the consumer pauses (30 seconds growing to 15 minutes, each under the broker's 30-minute
  consumer timeout) and returns the message, for as long as the outage lasts. Once the pauses stop
  growing it logs at Error. The delivery limit still bounds a message that breaks the consumer
  itself, because that path uses `basic.reject`.

The owner chose the outbox over an HTTP call and the text „Usunięte konto” over a per-account marker
at the #1071 gate.

### 5. Every finalizer run reconciles the outbox with the erased accounts

`ErasedAccountReconciler` runs at the end of every finalizer run, over **all** erased accounts:

- **It cuts every sent message of an erased account down to `{"IdentityUserId": …}`.** The row keeps
  its type, its times and the account id. One SQL statement does it. Unsent rows are left alone: the
  consumer would read a cut payload as poison, and deleting a row could race the relay. The next run
  cuts a row once the relay has sent it. Duplicate detection (ADR-0037) reads `InboxMessages`, never
  the outbox, so a cut row changes nothing for the consumer. A row that is not JSON is skipped
  (`pg_input_is_valid` behind a materialized CTE), so one bad row cannot stop every later run.
- **It writes an `AccountErased` for every erased account that has none.** That covers the accounts
  erased before this ADR. It is a check in every run and not a one-off migration on purpose: a
  migration would put rows of a type the previous release cannot route into the database, and a
  rolled-back relay would fail them on every pass.

An account counts as erased only with everything the erasure writes: the `anon-` address on the
anonymization domain, no password and a deletion date. The address alone is not proof, because the
registration form accepts any address.

### 6. A late e-mail change notice is not sent to an erased account

`EmailChangeCompletedProcessor` acknowledges without sending when the account is erased. Its message
could only arrive that late after a relay outage of 14 days or more, but then it would mail the
erased person's addresses again.

### 7. The grace period is at least 14 days

`GdprSettingsValidator` now refuses a `DeletionGracePeriod` shorter than the OpenIddict prune's
14-day retention. The policy promises 14 days, and the shorter values broke two things this ADR relies
on: every token of the account is old enough for the prune to delete it within a day of the erasure,
and no access token (five minutes) is still valid to write the name back into the TMS profile.

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
  accounts erased before the fix cleaned and announced once, a non-JSON row skipped, a second run
  changes nothing). The scan fails with `OutboxMessages.Payload` when the reconciliation is switched
  off.
- TMS integration: `EraseTranslatorProfileTests` (the database and the editor's author name),
  `Messaging/AccountErasedConsumerTests` (an event published like the relay erases the profile;
  poison is parked), `Messaging/AccountEventsTopologyTests` (against RabbitMQ 4.3.4: a nack never
  parks, a reject parks at the limit).
- Unit: `Translator.Erase`, the routing table, the consumer's pauses and payload checks, the late
  e-mail change notice, the grace-period rule.
