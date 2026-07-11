# ADR-0031: GDPR Account Deletion Runs Through a 14-Day Soft-Delete Grace Period

**Status:** Accepted
**Date:** 2026-07-11
**Decision-makers:** Solo maintainer (ticket #452, legal & GDPR compliance pack #459)
**Related:** `DeleteAccount` / `CancelAccountDeletion` (AuthSystem), `AccountErasureService`,
`AccountDeletionFinalizer`, TKS ADR-0017 (the ported original), tickets #452, #459

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
