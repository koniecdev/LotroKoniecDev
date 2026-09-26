# ADR-0048: An E-mail Change Is Undone From the Old Mailbox, With a Token No Stamp Rotation Can Kill

**Status:** Accepted (amended 2026-09-19 by #684 — the freed address is reserved, see rule 4 — and by
#685 — a scheduled deletion may not outlive the undo, see rule 5 — and by #864 — only a real
duplicate is reported as a taken address)
**Date:** 2026-08-20
**Decision-makers:** Solo maintainer (ticket #671, LEGAL-14, legal & GDPR pack #459; amendments #684, SEC-06
and #685, SEC-07)
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
`IDataProtector`, protecting `(createdAt, userId, revertStamp, purpose)`. Its purpose embeds both
addresses (`RevertEmailChange:{previous}->{new}`).

**Five rules. The first two were each defeated in review; the last two were added by later tickets,
after the same threat was found reaching the undo from a direction the token could not see.**

1. *Arm at most one undo per chain, at the address the chain started from.*
   `ApplicationUser.EmailChangeRevertTo` is set by the first change since the last revert and never
   overwritten. Only the change that armed it carries a link. This is what makes a second change
   worthless to an attacker: after A→B→C nothing is mailed to B, so there is no token in their
   hands at all.
2. *The row decides the destination, never the link.* A revert restores `EmailChangeRevertTo`, not
   the address the presented token names. Even a leaked token can only put the account back where
   the owner started.
3. *Rotate `EmailChangeRevertStamp` on a successful revert, and bake it into every token.* This is
   what makes a link single-use, together with the refusal when the account is already back on the
   armed address.
4. *Reserve the armed address for as long as the link lives* (added by #684, SEC-06). The original
   three rules all protect the token, and the attack that got through did not touch it: take the
   address instead. `RevertEmailChange` refuses when the armed address belongs to another account,
   and anyone can make that true with one anonymous `POST auth/register` — `CreateAsync` writes the
   row before any confirmation, `RequireConfirmedEmail` only blocks login, and nothing ever removes
   an unconfirmed registration. Free, permanent, no mailbox needed. So arming now also stamps
   `EmailChangeRevertArmedAt` and `NormalizedEmailChangeRevertTo`, and
   `IEmailChangeRevertReservation` refuses that address to `RegisterUser`, `RequestEmailChange` and
   `ConfirmEmailChange` for the revert token's own lifespan. It is one indexed lookup, it excludes
   the account that armed it, and it expires with the link, so no address is blocked for good.
   The refusal reuses `UserAlreadyExistsByEmail`, which leaks nothing: the address really was in
   use a moment earlier.
   The reservation cannot be turned against a stranger, and that is what makes it safe to have:
   arming an address means having confirmed it, because `SignIn.RequireConfirmedEmail` blocks the
   login and `RequestEmailChange` wants the password too. Nobody can reserve an address they do not
   already own.
   **Coming back settles the chain.** A confirm that lands the account on the armed address disarms
   the row and rotates `EmailChangeRevertStamp`, exactly as a successful revert does. Without it the
   row would stay armed at the first change's timestamp, and a later change away would still mint a
   fresh 14-day link off it — a link outliving the reservation meant to protect its address, which
   is this ticket's own bug again. Only someone reading the armed mailbox can reach that branch: the
   confirm link goes there. Re-arming then starts a new window, which is what rule 1 always meant —
   "never re-aim the target" is about where the undo points, not about when the chain ends.

5. *A scheduled deletion may not outlive the undo* (added by #685, SEC-07). Rule 4 protects the
   address; this one protects the account behind it. The revert cancels a scheduled deletion — that is
   already in the consequences below — but it can only do so while the link works, and the deletion
   window is anchored to a different event. An attacker changes the address at T0, waits until day 13,
   then schedules the deletion: the cancel link of ADR-0031 goes to the address they now hold, the
   owner's undo dies at T0+14, and the finalizer erases at about T+27. So while an undo is armed and
   unexpired the deletion-cancel e-mail now goes to `EmailChangeRevertTo` as well, carrying the same
   link, and the erasure waits for `max(scheduledAt + grace, armedAt + revertLifespan)`. The reasoning,
   the two rejected shapes and why the second half is a dormant invariant rather than the fix live in
   **ADR-0031's own amendment**, because it is that ADR's window that moved.
   How long "unexpired" means is `IEmailChangeRevertWindow`'s call, the same clock rule 4's reservation
   reads, so an address cannot be freed while the erasure still treats its link as live.

The two rejected shapes, kept because each looks correct until it is attacked:

- *"Refuse unless the account still sits on the address the token names."* An attacker chains
  A→B then B→C, and the owner's A→B link matches nothing.
- *"Restore from wherever the account sits, and rotate a stamp for single use."* Rotation is
  symmetric and the attacker held `Rev(B→C)` in their own mailbox, so they simply clicked first:
  the stamp rotated, the owner's link died, and the account was theirs at B with a password they
  chose. Whoever clicks first wins, and the attacker is not waiting for an e-mail.

Both fell to the same thing — a token that names its own destination is a bearer credential, and
after a chain of changes the attacker holds one. Rule 1 stops one being issued to them; rule 2
makes it useless if one ever is.

The four columns are **new nullable fields**, so this ADR no longer ships without a migration. They
are additive and expand-only, which is the cheapest shape ADR-0023 allows, and they buy the
guarantee the rest of this decision only claimed. Two arrived with the ADR
(`EmailChangeRevertStamp`, `EmailChangeRevertTo`) and two with rule 4
(`NormalizedEmailChangeRevertTo`, `EmailChangeRevertArmedAt`); the normalized twin exists so the
display form stays as the user typed it, exactly as Identity keeps `Email` beside
`NormalizedEmail`. The revert stamp is deliberately **not** the
security stamp: a password change rotates that, and surviving a password change is the one thing a
revert token must do.

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
- **The undo does not stop a live access token straight away.** `IUserSessionRevoker` kills the
  tokens in the database and the stamp rotation drops the auth cookie, but the TMS validates JWTs
  on its own, so a token already in the attacker's hands keeps working until it expires. That
  lifetime is **five minutes**, and this undo is the reason it is five and not sixty — ADR-0049,
  #686. The ability to mint a *new* token dies immediately.
- **A revert token outlives a password change by design.** That is the whole point, and it means
  the token cannot be cancelled by rotating the stamp — the usual lever in this codebase. Anyone
  touching `EmailChangeRevertTokenProvider` must understand that omitting the stamp is the
  requirement, not an oversight. What replaces it is `EmailChangeRevertStamp`, rotated on revert
  and only on revert.
- **A second change in a chain gets no undo link.** If the *owner* legitimately changes twice, the
  second change sends its notices without one, and their outstanding link still restores to where
  the chain started rather than to the intermediate address. Slightly surprising, and the safe way
  round: the alternative hands a working link to whoever holds the intermediate mailbox.
- **Unused tokens from before the last revert are dead, including ones their owner still wanted.**
  Rotating on revert is deliberately blunt: it cannot tell the attacker's token from a second
  legitimate one. A user who had two changes in flight and reverts one loses the other's link and
  must ask again. That is the right way round — a stale link that still works is what the second
  review found, twice.
- **The revert also cancels a scheduled deletion, and the deletion now waits for it.** It has to: rotating the security stamp kills
  ADR-0031's cancel token, and after an address change that cancel link was mailed to the address
  the account was moved to. Refusing the revert instead would leave the account locked, unable to
  log in or reset, and hard-erased by the finalizer — data loss reachable from one ordinary click.
  So this is a second, deliberate way back into a locked account, and it is exactly as strong as
  the first: proof of control over the mailbox the account came from. #685 completed the other
  direction: a deletion scheduled inside the undo window now mails its cancel link to the armed
  address too, and the erasure is held until the undo expires, so the cancellation this bullet
  promises cannot be timed out of reach (rule 5).
- **The old address learns the new one.** The warning and the notice both name it in full. In the
  attack case the recipient is the legitimate owner and needs it to act; in the normal case the
  recipient is the user themselves. Since #685 this reaches one step further than those two
  messages do: after a chain A→B→C only C is notified, so the deletion notice is the first thing
  telling A that the account now sits on C. It changes nothing substantive — rule 2 means whoever
  reads A can pull the account back from wherever it is, C included — but the disclosure is wider
  than "the address it moved to", and this is where that is written down.
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
- **An armed address is unavailable to everyone else for 14 days** (rule 4). Registering it,
  changing onto it and confirming a change onto it all answer "already in use", and to the person
  refused that is indistinguishable from a real account holding it. The window is bounded, it only
  ever covers an address that genuinely was in use minutes earlier, and the alternative — letting a
  stranger silently disarm somebody's only recovery path — is far worse.
- **Two gaps the reservation does not cover, both answered by the old refusal.** A row armed before
  #684 has no timestamp and is read as unreserved. And the arming is stamped at confirm time while
  the token is minted later, by the outbox processor at send time, so the reservation closes that
  much before the token does. Normally the gap is the dispatch delay, seconds; a failing relay
  stretches it, but delivery is capped at `RabbitMqTopology.EmailDeliveryLimit` (5) attempts before
  the message is dead-lettered, so it cannot grow to days. `RevertEmailChange` keeps refusing when
  the armed address belongs to somebody else, with its own page message, and the password is left
  alone.
- **The uniqueness check is application-level and racy.** `UserManager.UpdateAsync` validates with
  `FindByEmailAsync` *before* the write, so the unique index from `UniqueEmailIndex` is the only
  real arbiter. `UserStore.UpdateAsync` catches only `DbUpdateConcurrencyException`, so a Postgres
  `23505` surfaces as an unhandled `DbUpdateException`. Both write legs therefore carry
  `RegisterUser`'s `catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)`
  and turn the race into a `Result` failure instead of a 500.
  > **Amended 2026-09-26 (#864):** only a duplicate on an e-mail index is a lost race. Both legs
  > now catch just the `DbUpdateException` that the shared `IsTakenEmail` check places there. That check
  > is built on the `TakenAccountValueError` placement `RegisterUser` uses since #845. Any other save
  > error reaches the global handler as a 500
  > instead of being reported as a taken address.

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
- `EmailChangeCompletedProcessor` mints and sends a revert link only when `EmailChangeRevertTo`
  still names the address this message came from. A redelivery therefore cannot arm a second link,
  and a later change in the chain cannot arm one at all. Its unit tests must stub
  `UserManager.NormalizeEmail`: an unstubbed substitute returns null on both sides and
  `string.Equals(null, null)` is true, which silently walks every test past that branch.
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
