# ADR-0031: GDPR Account Deletion Runs Through a 14-Day Soft-Delete Grace Period

**Status:** Accepted (amended 2026-09-19 by #685 — the cancel link reaches the armed address and the
erasure waits for the undo; amended 2026-09-29 by #780 — the finalizer polls once a day; amended
2026-09-30 by #946 — the cap covers the grace period plus the poll interval; amended 2026-10-11 by
#947 — the cancel link ends at the scheduled time plus the grace period; amended 2026-10-17 by #971 —
the cap is 27 days, recorded in the #946 amendment; amended 2026-10-17 by #916 — the login page says
the deletion is under way once its date has passed; see the amendments below)
**Date:** 2026-07-11
**Decision-makers:** Solo maintainer (ticket #452, legal & GDPR compliance pack #459; amendment #685, SEC-07; amendment #780, #946, #947, #971, #916)
**Related:** `DeleteAccount` / `CancelAccountDeletion` (AuthSystem), `AccountErasureService`,
`AccountDeletionFinalizer`, `AccountDeletionSchedule`, ADR-0048 (the undo this window has to survive),
TKS ADR-0017 (the ported original), ADR-0035 (the Neon wake-up cost), ADR-0038 (tokens created at
delivery), tickets #452, #459, #685, #780, #946, #947, #971, #916

## Context

`DeleteAccount` performed **immediate, irreversible anonymization** the moment the user
submitted their password: auth-side PII wipe, permanent lockout, best-effort artifact
cleanup. The password was the only barrier (no MFA on the endpoint), so a single
credential-stuffing hit could erase an entire account with **zero recovery window** for the
legitimate owner.

GDPR does not require instant erasure: Art. 12(3) allows up to **one month**, and a
cancellation window is the industry standard (GitHub 90 d, Google 20–60 d, Discord
14–30 d). TheKittySaver replaced the exact same single-phase pattern with a two-phase flow
(TKS ADR-0017, 2026-07-11); this ADR ports that decision.

## Decision

Deletion is **two-phase with a 14-day cancellation window** (`Gdpr:DeletionGracePeriod`,
capped by options validation to stay inside Art. 12(3) — the cap is 27 days for the grace period plus
the poll interval (#946, #971), see the #946 amendment):

1. **Schedule (synchronous).** Password check → set
   `ApplicationUser.DeletionScheduledAt`, lock the account for the whole window
   (`LockoutEnd = scheduledAt + grace`), rotate the security stamp, best-effort revoke
   OpenIddict tokens/authorizations, and email a **one-time cancel link**. The link token
   comes from a dedicated `DataProtectorTokenProvider`
   (`AccountDeletionCancellationTokenProvider`) whose lifespan equals the grace period and
   which binds to the security stamp (single-use by rotation). If the email cannot be
   sent, the schedule is **unwound** — a grace window whose owner holds no cancel link is
   worthless. Response: `204` + `X-Deletion-Scheduled-At` / `X-Deletion-Finalizes-At`
   headers. Re-request while scheduled → `422 Auth.DeletionAlreadyScheduled` (the repo
   maps `DataConflict` → 422).
2. **Finalize (background).** `AccountDeletionFinalizerHostedService` polls
   (`Gdpr:DeletionFinalizationPollInterval`, default one day, `1.00:00:00`, since #780 — it
   was 1 h; first run at startup for post-downtime catch-up) for users with
   `DeletionScheduledAt + grace <= now` whose email lacks the anonymization marker, and runs
   the extracted erasure pipeline
   (`AccountErasureService`): auth anonymization → permanent lockout → artifact cleanup.
   Per-user failures are logged and retried on the next run; `DeletionScheduledAt` stays
   set after erasure as a non-PII audit trace.
3. **Cancel (anytime in the window).** `POST /auth/account/cancel-deletion` (anonymous,
   also driven by the hosted page `/Account/CancelDeletion`, which cancels only on POST so
   mail-scanner prefetches are harmless): validates the token, clears schedule + lockout,
   **removes the password hash** (the request may have come from whoever stole the
   password), rotates the stamp, and returns a fresh reset token that sends the user
   straight into the forced password-reset flow. Unknown email / bad / replayed token all
   collapse into one generic `Auth.InvalidCancelDeletionToken`.

**The emailed cancel link is the only recovery path.** During the window every other door
is shut: hosted login and the Testing password grant reveal a dedicated
"scheduled for deletion" state only *after* verifying the password (anti-enumeration);
`connect/authorize` terminates live session cookies of locked/scheduled accounts instead
of minting tokens; the refresh grant refuses them; forgot-/reset-password pretend success /
return the generic invalid-token error without doing anything; and change-password rejects
a still-valid pre-schedule JWT with `Auth.DeletionAlreadyScheduled` — its stamp rotation
would otherwise kill the emailed cancel token, the only recovery path.

**Deliberate deviation from TKS ADR-0017: no cross-context archival call.** TheKittySaver's
erasure pipeline first archives AdoptionSystem person data over HTTP. Here the
TranslationSystem stores only opaque `IdentityId` attribution references
(`SubmittedById`/`ApprovedById`), which become non-attributable the moment the auth user is
anonymized — no TMS-side call is needed, so the erasure service is auth-local and the
eventual-consistency failure mode TKS ticket #175 documented cannot occur.

> **Withdrawn by ADR-0065 (2026-10-09, #1071).** The premise was wrong from the start: since
> ADR-0004 the TMS keeps a `Translator` profile with the person's display name and e-mail, and
> `SubmittedById`/`ApprovedById` point at that profile. The erasure now writes an `AccountErased`
> outbox message in its anonymizing save, and the TMS erases the profile when it arrives. A TMS
> database outage delays that erasure; the consumer never gives up on it.

## Amendment (2026-09-19, #685 — SEC-07): the window has to survive an e-mail change

This ADR says the emailed cancel link is the only recovery path during the grace window, and it says
the erasure lands one grace period after the schedule. ADR-0048 later put a second recovery path next
to it — a 14-day undo mailed to the address an e-mail change left behind — and the two clocks did not
know about each other.

The attack that fell out of the gap: whoever holds the password moves the account to an address of
their own at T0, waits until day 13 of the owner's undo window, and only then schedules the deletion.
The cancel link goes to `user.Email`, which is theirs by now. The owner's undo link dies at T0+14.
The finalizer erases at about T+27, and `AccountErasureService` is not reversible. Every other door is
shut by design: the account is locked, `ChangePassword` answers `DeletionAlreadyScheduled`, and a
scheduled deletion refuses both legs of an e-mail change.

**Two rules close it.**

1. **The cancel link goes to the armed address too, while that undo is still live.**
   `AccountDeletionScheduledProcessor` sends a second, separately worded copy to
   `EmailChangeRevertTo`. It is the *same* link, carrying the account's **current** address, because
   `CancelAccountDeletion` resolves the account with `FindByEmailAsync` on that value — a link
   rewritten to the old address would verify against nothing. The armed address goes first, for the
   reason `EmailChangeCompletedProcessor` already gives: it is the one that can still save the
   account, so if only one of the two ever gets through it has to be that one.

   This hands the old mailbox no power it did not already hold. An armed address is by definition one
   the account confirmed, and it already holds a revert link that clears the password (ADR-0048
   rule 4). Past the undo window nothing is sent there, because past it this ADR's sibling has already
   conceded the account.

2. **The erasure waits for the undo:** `finalizesAt = max(scheduledAt + grace, armedAt + revertLifespan)`,
   read by the finalizer's query, by the `X-Deletion-Finalizes-At` header, by the lockout end, by the
   date in the e-mail and by the login page — `IAccountDeletionSchedule` owns both the scalar and the
   EF-translatable predicate, and a unit test checks the two readings against each other over a grid,
   because only one of them actually erases data.

   **Under the shipped configuration this rule never fires, and that is not a mistake.** A scheduled
   deletion already refuses `RequestEmailChange` and `ConfirmEmailChange`, so `armedAt <= scheduledAt`
   is an invariant, and with both clocks at 14 days the grace date always wins. Rule 1 is what closes
   the attack. Rule 2 is the invariant written down where it can be executed: it is what stops a
   shortened `Gdpr:DeletionGracePeriod` from erasing an account whose undo link still works, and it is
   why the date the header promises is the date the finalizer keeps.

**The hold cannot be pushed forward, so Art. 12(3) still holds.** A hold that a user could keep
extending would be a way to refuse erasure for ever. It is not one: `RequestEmailChange` and
`ConfirmEmailChange` both refuse while a deletion is scheduled, and a revert disarms the row as it
cancels, so `EmailChangeRevertArmedAt` cannot move once a deletion is pending. With
`armedAt <= scheduledAt` the whole rule collapses to
`finalizesAt <= scheduledAt + max(grace, revertLifespan)`, and the cap in `GdprSettingsValidator`
bounds the grace term exactly as it did before this amendment. (Since #946 the cap counts the poll
interval too, and since #971 it is 27 days, not 30; see the #946 amendment.)

The term that cap does **not** cover is the other one. `EmailChangeRevertTokenProviderOptions` is
registered with a plain `AddOptions` and no `BindConfiguration`, so its 14 days can only change in
code — but nothing validates it, and a lifespan raised past 27 days would push `finalizesAt` past the
cap that stands in for the month this ADR is written to respect. Whoever changes that number owns this
paragraph. (This count leaves out the poll interval. Counting it, the shipped 14 days is about the
most one month allows; the #946 amendment has the numbers.)

**What the fix does not claim.** The owner who cancels from the old mailbox stops the erasure and
destroys the password, but the account still sits on the address it was moved to, and whoever reads
that mailbox can reset the password and schedule again. That is a stalemate, not a recovery — and a
stalemate in which nothing is erased, which is the acceptance criterion. The full recovery is the
undo link of ADR-0048, and it is why rule 1 only runs while that link is alive. Mailing a *fresh*
revert link instead was considered and rejected: it would recover the account outright, but it
extends the undo past its 14 days and drags #684's address reservation along with it.

Refusing to schedule a deletion at all while an undo is armed was the other candidate — simpler, and
symmetric with the existing "a scheduled deletion blocks the e-mail change" rule. Rejected: it makes
an honest user who just changed their address wait up to 14 days to delete, and 14 + 27 days at this
ADR's own configured cap (grace plus poll since #946, 27 days since #971) would break the Art. 12(3)
budget that cap exists to protect. It is also the "block the action" shape this ADR and ADR-0048 have
now both declined twice.

## Amendment (2026-09-29, #780): the finalizer polls once a day

The finalizer used to poll every hour. Each run wakes the Neon compute, and at ADR-0035's figure of
about 0.02 CU-h per wake-up that was about 14 CU-h a month in each environment — six times what
ADR-0035 lets the outbox safety sweep cost. So `Gdpr:DeletionFinalizationPollInterval` is now one day
(`1.00:00:00` in `appsettings.json`; `24:00:00` would bind to 24 days). An account is erased at most
a day after the date this ADR promises, never before it, and the startup run still catches up after
a deploy.

**"The finalizer keeps the date" now means "never before it".** Rule 2 of the #685 amendment, and the
comments on `IAccountDeletionSchedule`, `AccountDeletionFinalizer` and `DeleteAccount`, say the
finalizer keeps the date the header, the e-mail and the login page promise. It still does in the
sense that matters: all of them read one rule, so nothing is erased early. It lands up to a day
later. The e-mail, the confirmation page and the login page print that date to the minute, so the
minute now means "not before this minute".

Nothing the user can act on waits for a run. The cancel link ends at the scheduled time plus the
grace period. (When this amendment was written, the link lived one grace period from the moment its
e-mail was built, so a late e-mail moved its end. #947 replaced that rule; see the #947 amendment.)
Every door checks `DeletionScheduledAt`, not the lockout, so the account stays shut after
`LockoutEnd` has passed too. The longer gap between the date and the erasure shows in two places: the address
stays taken, so registering it again fails until the run, and the login page showed a date that had
passed next to a dead cancel link. #916 replaced that message; see the #916 amendment. A run that
fails is now retried a day later, not an hour later (#937).

**The validator ties the two settings together.** The poll interval must not exceed the grace
period, so an environment that shortens `Gdpr:DeletionGracePeriod` below one day, to watch a real
erasure in QA for example, must shorten `Gdpr:DeletionFinalizationPollInterval` too. Otherwise the
auth API refuses to start, with a message that names both settings.

## Amendment (2026-09-30, #946): the cap covers the grace period plus the poll interval

An account is erased at the first run after its date, so the erasure can land up to one poll interval
after it. A 30-day cap on the grace period alone let the erasure land on day 31. So the validator
now caps `Gdpr:DeletionGracePeriod` plus `Gdpr:DeletionFinalizationPollInterval`, with a message that
names both settings. This rule replaces the old 30-day cap on the grace period alone, because it
implies it. The cap was 30 days when this amendment was written. On 2026-10-17 #971 lowered it to 27
days, for the reason in the last paragraph. With the shipped 14 days and one day, the erasure lands
by day 15. With a daily poll, the longest grace period the auth API starts with is 26 days.

The undo term of the #685 amendment stays inside one month too, but not always inside the 27 days. It
wins only when the grace period is shorter than the 14-day undo window. The poll interval is then at
most the grace period, and the two together are at most 27 days, so the poll interval is at most 13.5
days. The erasure then lands at most 27.5 days after the request. That is half a day past the cap,
but still inside the shortest month: 28 days, or 27 days and 23 hours in Poland time when that month
spans the spring clock change. Only a shortened grace period with a poll interval almost as long
reaches that corner. With the daily poll, the undo hold puts the erasure by day 15. The rule that the
poll interval must not exceed the grace period is what carries this: without it, a 1-day grace period
and a 26-day poll would pass the sum and let the undo hold push the erasure up to about day 40. The
erasure stays inside every month only while the undo lifespan stays under 14 days and 11 hours, so the
shipped 14 days must not grow. It stays inside the 27-day cap only at 13.5 days or less.

Two delays are outside what a startup check can bound. A run that fails adds one poll interval each
time (#937). An app that is down erases nothing until the catch-up run when it starts again.

**27 days stands in for Art. 12(3)'s "one month" (#971, owner decision 2026-10-11).** A period in
months ends on the same date in the last month, or on that month's last day when the date does not
exist (EU Regulation 1182/71). So one calendar month is 28 to 31 days. The first cap of 30 days was
inside the month for most requests, but not for one made in February or on the last days of January:
with the longest settings the auth API started with, a request made on 1 February could be erased on
3 March. Twenty-seven days fits in every calendar month, with a day to spare for the daily deletion
run, as the owner put it. The poll interval already counts inside the 27 days, so the first run does
not use that spare day: it is extra room.

## Amendment (2026-10-11, #947): the cancel link ends with the grace period, however late its e-mail went out

The cancel token is created when its e-mail is built, and it lives one grace period from then
(ADR-0038 decision 2). That is normally seconds after the schedule. After a mail-queue outage it can
be hours later, and the link then kept working for those hours after "scheduled + grace", the date
this ADR calls the end of the window. Since #780 the erasure runs only once a day, so a late link could
still cancel a deletion whose date had passed, until the next run.

**The owner's decision: the cancel link works until the scheduled time plus the grace period, and not
longer.** `CancelAccountDeletion` now refuses a valid token once
`IAccountDeletionSchedule.CancellableUntil(DeletionScheduledAt)` has passed. The answer is the generic
`Auth.InvalidCancelDeletionToken`, the same as for an unknown address, a bad token or a used one, so
the endpoint still tells nobody the state of an account, and the hosted page shows its usual "invalid
or expired" text. One rule, `IAccountDeletionSchedule.IsCancellableAt`, answers "does the link still
work" for the endpoint and for the deletion-scheduled e-mail worker, which already refused to send once
the window was over.

**This link does not follow ADR-0038's "the countdown starts at delivery".** Its token is still
created at delivery, for the reasons ADR-0038 gives: no working token sits in an outbox row, and the
token is tied to the current security stamp. Only its end is fixed by the schedule. ADR-0038 §2
carries the matching exception note.

The cut-off is `CancellableUntil`, not `FinalizesAt`. The two differ only while an armed undo of
ADR-0048 outlasts the grace period, which the shipped configuration never reaches (see the #685
amendment), and in that tail the undo link still cancels the deletion. What the login page and the
dead-link page say once the link has stopped working is #916.

## Amendment (2026-10-17, #916): the login page once the deletion date has passed

After its date, the account can wait up to a day for the daily run (the #780 amendment). The login page
used to show the date that had passed next to the advice to click the cancel link, which was dead.

**The owner's decision (2026-10-11):**

- Once `FinalizesAt` has passed, the login page says "Usuwanie Twojego konta jest w toku i nie można go
  już anulować." with no date. It uses the same boundary as the finalizer's `IsDueBy`. Before that
  moment the page still names the date and the cancel link.
- The dead-link page stays as it is. Its sentence, "działa do dnia zaplanowanego usunięcia konta", is
  true since #947.
- No text points to the e-mail change undo link. The window where the cancel link is dead but the undo
  link still works never opens with the shipped settings (see the #685 amendment).

## Consequences

- Account takeover can no longer irreversibly destroy an account: the attacker's deletion
  locks the account but the owner cancels via email and resets the password.
- Deletion is no longer instant — the privacy policy explicitly discloses the 14-day window
  (transparency, Art. 13/14). GDPR compliance is preserved (well under the 1-month limit).
- The email becomes a hard dependency of *scheduling* (send failure rolls the schedule
  back); SMTP outages surface as `Auth.DeletionSchedulingFailed`, not as silent data loss.
- Half-erased states self-heal: the finalizer retries until the pipeline completes.
- The old single-phase erasure tests are replaced by schedule/cancel/finalizer
  integration suites; erasure E2E with real elapsed time is deliberately not attempted
  (integration > E2E per the testing philosophy).
- Reminder email 24 h before finalization is left out (same cut as TKS) — a follow-up
  ticket can add it to the finalizer loop. *(Since #780 that loop runs once a day, so it cannot
  send a mail 24 h ahead on time; a reminder needs its own schedule.)*
