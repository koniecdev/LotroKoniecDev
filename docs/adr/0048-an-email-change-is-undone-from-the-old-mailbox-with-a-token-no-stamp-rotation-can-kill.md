# ADR-0048: An E-mail Change Is Undone From the Old Mailbox, With a Token No Stamp Rotation Can Kill

**Status:** Accepted
**Date:** 2026-08-20
**Decision-makers:** Solo maintainer (ticket #671, LEGAL-14, legal & GDPR pack #459)
**Related:** ADR-0031 (the same threat, answered for deletion), ADR-0038 (the one dispatch
pipeline every e-mail here rides), ADR-0046 (what may be said behind a verified password),
spec 0013, `Features/Auth/RequestEmailChange`, `Pages/Account/ConfirmEmailChange`,
`Pages/Account/RevertEmailChange`, `Persistence/Identity/EmailChangeRevertTokenProvider`

## Context

The e-mail address is the login identifier: `LoginModel` resolves the account with
`FindByEmailAsync`. So "change your e-mail" is really "change the credential you sign in with",
and LEGAL-14 asks us to expose it as self-service, because the privacy policy §06 already promises
rectification "w ustawieniach konta".

The obvious flow — current password, then a verification link clicked in the new mailbox — has a
hole the maintainer named directly: **the password is the only barrier, and the password is
exactly what gets stolen.** An attacker who credential-stuffs their way in changes the address to
one they own, clicks their own link, and the real owner is locked out permanently. They cannot log
in (wrong address), cannot reset a password (the reset link goes to the attacker), and cannot use
the deletion-cancel path (nothing was deleted). Support e-mail is the only way back, which is what
the ticket set out to remove.

This is not a new problem in this repo. ADR-0031 hit the identical shape — "a single
credential-stuffing hit could erase an entire account with zero recovery window" — and answered
it without adding a second factor the product does not have: **do not block the action, give the
mailbox the attacker does not control a single-use way to undo it, and destroy the stolen
credential on the way out.** `CancelAccountDeletion` nulls `PasswordHash` for exactly that reason.

Requiring the *old* mailbox to approve before anything moves would close the hole completely, and
was rejected: "I no longer have access to my old mailbox" is the single most common legitimate
reason to change an e-mail address, and gating on that mailbox turns the feature back into a
support ticket for the people who need it most.

One detail decides whether the undo pattern actually works here. Identity's
`DataProtectorTokenProvider` writes the user's security stamp into every token and refuses it once
the stamp moves. Changing the password rotates the stamp — and changing the password is precisely
the attacker's next move after taking over the address. A stamp-bound revert token is dead by the
time the victim reads their mail.

## Decision

**An e-mail change proceeds on password + a click in the new mailbox, and the old mailbox holds a
14-day, single-use revert link that restores the address and destroys the password.**

1. **Request** (`POST auth/account/change-email`, authorized, 3/hour per IP). Password verified,
   new address checked free, a scheduled deletion refuses the whole thing (ADR-0031's lockout must
   stay reachable). Nothing on the user row changes, so the handler saves the outbox row itself —
   `ForgotPassword` is the shape to copy, not `DeleteAccount`. One `EmailChangeRequested` row,
   carrying **both** the current and the target address. Its processor sends **two** e-mails: the
   verification link to the new address, and a warning to the old one naming the target, so the
   owner learns of an attack while the link is still unclicked.
2. **Confirm** (`/Account/ConfirmEmailChange`, anonymous, token-authorized). **GET renders a
   confirmation form; the POST does the work.** The token is a stamp-bound
   `EmailChangeTokenProvider` whose purpose embeds the target address, so editing the address in
   the URL fails verification. The POST verifies the token *first*, then in **one** save: new
   address, `EmailConfirmed = true`, security stamp rotated, and an `EmailChangeCompleted` outbox
   row. Then `IUserSessionRevoker` revokes the OpenIddict tokens and authorizations.
3. **Notify + arm the undo.** `EmailChangeCompleted` carries both addresses. Its processor sends a
   notice to the new address and, to the **old** address, a notice carrying the revert link.
4. **Revert** (`/Account/RevertEmailChange`, anonymous, token-authorized, 14 days). **GET renders a
   confirmation form; the POST does the work.** It restores the previous address *from wherever the
   account currently sits*, sets
   `PasswordHash = null`, rotates the stamp, revokes sessions and hands the visitor into the
   password-reset flow — `CancelAccountDeletion`'s playbook, verbatim. The stolen password stops
   working at that moment.

**Neither page may mutate on `GET`.** `CancelDeletion.cshtml.cs` states the reason in its own doc
comment: *"A GET only shows a confirmation form, so a mail scanner that opens the link cancels
nothing."* The revert link lands in a **live** mailbox, and corporate mail security (SafeLinks,
Proofpoint, Mimecast) fetches every URL it sees. A GET-mutating revert page would undo every
legitimate e-mail change, and null the password, seconds after the notice arrived.
`ConfirmEmail.cshtml.cs` does mutate on `GET`; it is the older outlier, not the pattern to copy.

**The revert token is deliberately not bound to the security stamp.**
`EmailChangeRevertTokenProvider` is our own `IUserTwoFactorTokenProvider<ApplicationUser>` over an
`IDataProtector`, protecting `(createdAt, userId, purpose)` and nothing else. Its purpose embeds
both addresses (`RevertEmailChange:{previous}->{new}`).

**Two guards, because one is not enough — and this is where the "no schema change" version of this
ADR failed twice.**

1. *Restore from wherever the account sits.* Refuse only when the account is **already back** on
   `previous`. Requiring it to still sit on the address the token names is the trap: an attacker
   who knows the password changes the address twice, A→B then B→C, and the owner's A→B link matches
   nothing while the fresh revert offer for B→C is delivered to B, which the attacker owns.
2. *Rotate `ApplicationUser.EmailChangeRevertStamp` on a successful revert, and bake it into every
   revert token.* Guard 1 alone opens the mirror attack: after that same chain the attacker holds
   `Rev(B→C)` in **their** mailbox, so once the owner reverts and resets their password, the
   attacker fires their own token, the account moves back to B, the password is cleared again and
   the page hands them a live reset link — silently, because the revert leg sends no e-mail. Every
   revert token is a bearer credential to move the account to its previous address, and nothing on
   the row distinguishes the owner's from the attacker's. Rotating retires the whole chain at once
   and is what actually makes a link single-use.

The stamp is a **new nullable column**, so this ADR no longer ships without a migration. It is
additive and expand-only, which is the cheapest shape ADR-0023 allows, and it buys the guarantee
the rest of this decision only claimed. It is deliberately **not** the security stamp: a password
change rotates that, and surviving a password change is the one thing a revert token must do.

## Consequences

**Good**

- The threat the ticket could not otherwise answer is answered: an attacker with the password but
  not the old mailbox holds the account for at most 14 days, and loses the password when the owner
  clicks. Two e-mails have to be missed for the takeover to stick.
- The legitimate "I lost my old mailbox" case still self-serves. Nothing in the flow requires
  reading the old inbox.
- No new factor and no new infrastructure. The pending change itself still lives in the token — no
  `PendingEmail` column, no audit table. The one schema cost is a single nullable
  `EmailChangeRevertStamp`, additive and expand-only (ADR-0023's cheapest shape).
- It mirrors ADR-0031, so a reader who understands deletion already understands this.

**Bad / accepted**

- **The window is not a lock.** For up to 14 days the attacker really does control the account and
  can read and write translations. We accept it: the alternative locks out the users the feature
  exists for. Deletion could afford the lockout because the account was on its way out anyway.
- **A revert token outlives a password change by design.** That is the whole point, and it means
  the token cannot be cancelled by rotating the stamp — the usual lever in this codebase. Anyone
  touching `EmailChangeRevertTokenProvider` must understand that omitting the stamp is the
  requirement, not an oversight, and the guard that replaces it is the not-already-back check on
  the revert page.
- **Unused tokens from before the last revert are dead, including ones their owner still wanted.**
  Rotating on revert is deliberately blunt: it cannot tell the attacker's token from a second
  legitimate one. A user who had two changes in flight and reverts one loses the other's link and
  must ask again. That is the right way round — a stale link that still works is what the second
  review found, twice.
- **The revert also cancels a scheduled deletion.** It has to: rotating the security stamp kills
  ADR-0031's cancel token, and after an address change that cancel link was mailed to the address
  the account was moved to. Refusing the revert instead would leave the account locked, unable to
  log in or reset, and hard-erased by the finalizer — data loss reachable from one ordinary click.
  So this is a second, deliberate way back into a locked account, and it is exactly as strong as
  the first: proof of control over the mailbox the account came from.
- **The old address learns the new one.** The warning and the notice both name it in full. In the
  attack case the recipient is the legitimate owner and needs it to act; in the normal case the
  recipient is the user themselves.
- **At-least-once delivery can double a send.** `EmailChangeRequested` sends two e-mails from one
  message, so a failure on the second retries the first. Duplicated warnings and duplicated
  verification links are harmless — a resent link is the same link — which is the bar ADR-0038's
  registry sets for any new message type.
- **A password change kills a *pending* change**, because the confirm token is stamp-bound. The
  user simply asks again. Only the revert token is exempt.
- **A lost data-protection keyring kills a revert link with no way to reissue it.** The runbook
  already notes that losing the auth keyring breaks antiforgery plus the password-reset and
  confirmation links — but those live 24 h, so the damage self-heals in a day. A revert token lives
  14 days and, because this ADR deliberately persists nothing, there is no server-side record from
  which to mint a replacement. The keyring volume (M6-04 / ADR-0005) is now load-bearing for two
  weeks instead of one day.
- **The uniqueness check is application-level and racy.** `UserManager.UpdateAsync` validates with
  `FindByEmailAsync` *before* the write, so the unique index from `UniqueEmailIndex` is the only
  real arbiter. `UserStore.UpdateAsync` catches only `DbUpdateConcurrencyException`, so a Postgres
  `23505` surfaces as an unhandled `DbUpdateException`. Both write legs therefore carry
  `RegisterUser`'s `catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)`
  and turn the race into a `Result` failure instead of a 500.

## Alternatives Considered

- **Old address approves first, then the new one confirms.** Closes the hole outright. Rejected:
  it makes the feature unusable for anyone who lost the old mailbox, which is the main reason
  people change an address at all. It would trade one support-ticket-only population for another,
  larger one.
- **Notify the old address and offer nothing to click.** The smallest diff and what GitHub-class
  products do. Rejected on the maintainer's own objection: a notice the owner misses, or cannot
  read, leaves no way back, and this repo already refused that trade for deletion.
- **A revert link built on `DataProtectorTokenProvider` like every other token here.** Rejected on
  a mechanism check, not on taste: it embeds the security stamp, and the attacker rotates the stamp
  by changing the password. The link would be dead before it was useful.
- **Persist the pending change and the revert secret in columns** (`PendingEmail`,
  `EmailRevertTokenHash`, `EmailRevertUntil`). Robust and inspectable, and it would give a real
  audit row. Rejected as YAGNI for now: it buys nothing the token does not already give, and it
  costs a migration plus expand/backfill discipline on a ~zero-user table. Revisit if we ever need
  to *show* a pending change in the UI or to cancel one administratively.
- **Step up with a second factor.** There is no second factor in the product. Out of scope.
- **Use Identity's built-in `GenerateChangeEmailTokenAsync` / `ChangeEmailAsync` for the confirm
  leg.** Both are public, and `ChangeEmailAsync` sets the address, sets `EmailConfirmed`, rotates
  the stamp and saves **once** — so it does *not* split the mutation from the outbox row, and an
  earlier draft of this ADR was wrong to say it did. Rejected for one narrower reason:
  `ChangeEmailAsync` verifies the token *inside* itself, and Identity's purpose string
  (`"ChangeEmail:" + newEmail`) is a `private static` a caller cannot reproduce. So the only
  available order is enqueue-then-call, and an invalid token returns before saving — leaving a
  tracked `EmailChangeCompleted` row in a request-scoped `AuthDbContext` that OpenIddict also
  writes through, where a later flush can send a "your address changed" e-mail for a change that
  never happened. Owning the provider lets the handler verify first, then commit the row and the
  mutation together, which is the `DeleteAccount` / `CancelAccountDeletion` shape this repo already
  uses for exactly this problem.

## Implementation Notes

- `EmailChangeTokenProvider` (confirm leg) is an ordinary `DataProtectorTokenProvider`, registered
  like `AccountDeletionCancellationTokenProvider`, lifespan 24 h to match the activation link the
  users already know. It exists so the handler can call `VerifyUserTokenAsync` **before** it
  enqueues anything — see the rejected alternative above, not because Identity's own API is
  unavailable or transactionally worse.
- Do **not** set `NormalizedEmail` by hand. `UserManager.UpdateAsync` runs
  `UpdateNormalizedEmailAsync` after validation and before the write, so a hand-set value is
  overwritten either way.
- `EmailChangeRevertTokenProvider` (revert leg) is hand-written over `IDataProtector`, lifespan
  14 days, carrying `(createdAt, userId, revertStamp, purpose)`. Its unit tests must pin **both**
  halves: a *security* stamp rotation does not invalidate it, and a *revert* stamp rotation does.
  Those two assertions are this ADR, executable.
- `EmailChangeCompletedProcessor` skips a redelivery whose account has already moved on, so a
  dead-lettered replay cannot mint a fresh token and restart the fourteen days.
- Every value that arrives in one of these links is checked for shape before the page prints it or
  acts on it. The user id must parse as a `Guid` — Identity converts it to the key type before it
  queries, so a non-GUID throws inside the store and a bad link becomes a 500 on a page somebody
  opened from their inbox — and the addresses must match `EmailConstants.RegexPattern`, which also
  keeps arbitrary text off a branded page that carries a password-clearing button.
- Both new outbox types get a `RabbitMqTopology` routing key under `email.*`. The queue binds
  `email.#`, so no binding or queue-argument change and no `PRECONDITION_FAILED` risk (ADR-0036).
- Neither leg may call SMTP on a request path; both go through the outbox (ADR-0038).
  `ResendEmailConfirmation` stays the one deliberate exception in this system.

## References

- Ticket #671 (LEGAL-14), spec 0013
- ADR-0031 (deletion grace period — the same threat, the same answer)
- ADR-0038 (one dispatch pipeline for every transactional e-mail)
- Google's 7-day "recover your account" window and Apple's rescue-address pattern are the
  consumer-scale precedents for undo-from-the-old-mailbox.
