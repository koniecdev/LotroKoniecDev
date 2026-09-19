# ADR-0031: GDPR Account Deletion Runs Through a 14-Day Soft-Delete Grace Period

**Status:** Accepted (amended 2026-09-19 by #685 — the cancel link reaches the armed address and the
erasure waits for the undo, see the amendment below)
**Date:** 2026-07-11
**Decision-makers:** Solo maintainer (ticket #452, legal & GDPR compliance pack #459; amendment #685, SEC-07)
**Related:** `DeleteAccount` / `CancelAccountDeletion` (AuthSystem), `AccountErasureService`,
`AccountDeletionFinalizer`, `AccountDeletionSchedule`, ADR-0048 (the undo this window has to survive),
TKS ADR-0017 (the ported original), tickets #452, #459, #685

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
capped at 30 days by options validation to stay inside Art. 12(3)):

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
   (`Gdpr:DeletionFinalizationPollInterval`, default 1 h; first run at startup for
   post-downtime catch-up) for users with `DeletionScheduledAt + grace <= now` whose email
   lacks the anonymization marker, and runs the extracted erasure pipeline
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
`finalizesAt <= scheduledAt + max(grace, revertLifespan)`, and the 30-day cap in
`GdprSettingsValidator` bounds it exactly as it did before this amendment. The one thing to check if
that cap is ever raised past the undo's 14 days is this sentence, not the code.

**What the fix does not claim.** The owner who cancels from the old mailbox stops the erasure and
destroys the password, but the account still sits on the address it was moved to, and whoever reads
that mailbox can reset the password and schedule again. That is a stalemate, not a recovery — and a
stalemate in which nothing is erased, which is the acceptance criterion. The full recovery is the
undo link of ADR-0048, and it is why rule 1 only runs while that link is alive. Mailing a *fresh*
revert link instead was considered and rejected: it would recover the account outright, but it
extends the undo past its 14 days and drags #684's address reservation along with it.

Refusing to schedule a deletion at all while an undo is armed was the other candidate — simpler, and
symmetric with the existing "a scheduled deletion blocks the e-mail change" rule. Rejected: it makes
an honest user who just changed their address wait up to 14 days to delete, and 14 + 30 days at this
ADR's own configured cap would break the Art. 12(3) budget that cap exists to protect. It is also the
"block the action" shape this ADR and ADR-0048 have now both declined twice.

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
  ticket can add it to the finalizer loop.
