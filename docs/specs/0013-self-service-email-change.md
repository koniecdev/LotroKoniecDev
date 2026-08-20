# Spec 0013: Self-service e-mail change from "Moje konto"

- **Status:** Agreed
- **Date:** 2026-08-20
- **Author:** koniecdev
- **Ticket:** #671 (LEGAL-14)
- **Related:** **ADR-0048** (the guard decision: undo from the old mailbox, with a token no stamp
  rotation can kill), spec 0009 (Moje konto GDPR self-service — this is its explicit "e-mail
  change" out-of-scope item), ADR-0031 (two-phase deletion — the same threat, answered first),
  ADR-0035/0036/0037/0038 (the outbox → broker → processor e-mail pipeline), ADR-0040
  (authorization-aware HATEOAS links), ADR-0044 (frontend owns Polish copy), ADR-0046 (what may be
  said behind a verified password).
  Siblings to mirror: `Features/Auth/ChangePassword.cs` (the authorized command),
  `Features/Auth/ForgotPassword.cs` (a pure-send handler that saves its own outbox row),
  `Features/Auth/CancelAccountDeletion.cs` (verify token → mutate → outbox in one save),
  `Pages/Account/CancelDeletion.cshtml(.cs)` (**the page shape: GET form, POST action**),
  `Components/Pages/Account/ChangePassword.razor`,
  `Persistence/Identity/AccountDeletionCancellationTokenProvider.cs`.

## Business context

The privacy policy already promises rectification as self-service: §06 says *"Prawo do
sprostowania — poprawisz nieaktualne lub błędne dane w ustawieniach konta."* Today nothing in
"Moje konto" is editable, so that sentence is false and art. 16 RODO is only reachable by
e-mailing the owner. The e-mail address is also the **login identifier** (`LoginModel` looks the
user up with `FindByEmailAsync`), so a translator who loses access to their mailbox loses access
to the account with no way back that does not involve a human.

Spec 0009 deliberately parked "e-mail change" as out of scope when it built the account section.
This spec is that follow-up: the last self-service GDPR right the policy claims and the product
does not deliver.

The naive flow — password plus a click in the new mailbox — hands the account to anyone who steals
the password, permanently. ADR-0048 records why we answer that with an undo from the old mailbox
rather than with an approval gate on it.

## Goal

A signed-in translator can change the e-mail address they log in with, from "Moje konto", proving
it with their current password and with a click on a link sent to the **new** address. The old
mailbox is told before and after, and holds a 14-day single-use link that undoes the change and
destroys the password that authorised it.

## In scope

### The four legs

```
REQUEST   POST auth/account/change-email      password + new address, authorized, 3/h per IP
          → warning e-mail        → OLD inbox (names the target address)
          → verification link     → NEW inbox (24 h)

CONFIRM   GET  /Account/ConfirmEmailChange    renders a confirmation form — MUTATES NOTHING
          POST /Account/ConfirmEmailChange    token verified first, then ONE save:
          address moves · EmailConfirmed = true · stamp rotated · outbox row
          → sessions revoked
          → notice                → NEW inbox
          → notice + REVERT link  → OLD inbox (14 days)

REVERT    GET  /Account/RevertEmailChange     renders a confirmation form — MUTATES NOTHING
          POST /Account/RevertEmailChange     address restored · PasswordHash = null
          → stamp rotated · sessions revoked · password-reset flow
```

**Neither page may mutate on `GET`.** `Pages/Account/CancelDeletion.cshtml.cs` says why in its own
doc comment: *"A GET only shows a confirmation form, so a mail scanner that opens the link cancels
nothing."* The revert link lands in a **live** mailbox, and corporate mail security fetches every
URL it sees — a GET-mutating revert page would undo every legitimate change and null the password
seconds after the notice arrived. `ConfirmEmail.cshtml.cs` mutates on `GET`; it is the older
outlier and explicitly **not** the pattern here.

### Auth API (`LotroKoniecDev.AuthSystem.API`)

- **`Features/Auth/RequestEmailChange.cs`** — `POST auth/account/change-email`,
  `RequireAuthorization()`, mirroring `ChangePassword`. Command
  `(UserId, NewEmail, CurrentPassword, IpAddress, UserAgent)`; FluentValidation validator for the
  command (`EmailConstants` rules on `NewEmail`, non-empty password). The handler verifies the
  password, refuses while a deletion is scheduled, refuses the caller's own current address,
  refuses an address another account holds, and enqueues one outbox row. It **does not mutate the
  user**, so — like `ForgotPassword.cs:90-91` and unlike `DeleteAccount` — it injects
  `AuthDbContext` and calls `SaveChangesAsync` itself before `NotifyEnqueuedCommitted()`.
  `OutboxWriter.Enqueue` only `Add`s; a handler that forgets the save sends nothing.
- **`Features/Auth/ConfirmEmailChange.cs`** and **`Features/Auth/RevertEmailChange.cs`** — the two
  page legs as `ICommandHandler` slices, so the PageModels stay thin (CLAUDE.md: "handlers are
  orchestrators"). `CancelDeletion.cshtml.cs` + `CancelAccountDeletion.cs` is the exact pair to
  mirror; `ConfirmEmail.cshtml.cs`, which inlines its logic, is not.
- **`Persistence/Identity/EmailChangeTokenProvider.cs`** — our own
  `DataProtectorTokenProvider<ApplicationUser>` + options class, exactly like
  `AccountDeletionCancellationTokenProvider`. `ProviderName = "EmailChange"`,
  `PurposeFor(newEmail) => $"ChangeEmail:{newEmail}"`, lifespan **24 h**. It exists so the handler
  can `VerifyUserTokenAsync` **before** it enqueues anything: Identity's own `ChangeEmailAsync`
  verifies internally against a `private static` purpose string, forcing an enqueue-then-call
  order that leaves a tracked outbox row behind on a bad token (ADR-0048 §Alternatives). It is
  *not* because Identity's API is unavailable — `GenerateChangeEmailTokenAsync` and
  `ChangeEmailAsync` are both public and both save exactly once.
- **`Persistence/Identity/EmailChangeRevertTokenProvider.cs`** — hand-written
  `IUserTwoFactorTokenProvider<ApplicationUser>` over `IDataProtector`, protecting
  `(createdAt, userId, revertStamp, purpose)` and **no security stamp**, lifespan **14 days**,
  `PurposeFor(previous, next) => $"RevertEmailChange:{previous}->{next}"`. ADR-0048 §Decision
  explains why the stamp must be left out; a unit test pins that a stamp rotation does not kill it.
  `AddTokenProvider` type-checks `IUserTwoFactorTokenProvider<TUser>`, not
  `DataProtectorTokenProvider`, so it registers exactly like the existing one.
- **Outbox contracts** `Outbox/EmailChangeRequested.cs`
  (`Guid IdentityUserId, string CurrentEmail, string NewEmail`) and `Outbox/EmailChangeCompleted.cs`
  (`Guid IdentityUserId, string PreviousEmail, string NewEmail`). Both carry addresses because —
  unlike `EmailConfirmationRequested` — reading them off the user row at send time is wrong: after
  a delayed or dead-lettered redelivery the row already holds the **new** address, and a warning
  meant for the old mailbox would go to the attacker. The **tokens** are still created by the
  processor at send time, so "the link expires 24 h from when you receive this" stays true.
- **Routing** — two entries in `OutboxMessageRouting` and two routing keys in `RabbitMqTopology`
  (`email.change-requested`, `email.change-completed`). The queue binds `email.#`, so no binding
  or queue-argument change, hence no `PRECONDITION_FAILED` risk (ADR-0036).
- **Processors** `Services/Emails/EmailChangeRequestProcessor.cs` (verification link → new,
  warning → old; skips when `user.Email` no longer equals `CurrentEmail`, because the request is
  already resolved) and `EmailChangeCompletedProcessor.cs` (notice → new, notice + revert link →
  old), registered as keyed `IEmailMessageProcessor` (ADR-0038). Both are safe to run twice.
- **Sender + link factories** `Services/Emails/IEmailChangeEmailSender` + `EmailChangeEmailSender`
  (four templates), `EmailChangeVerificationLinkFactory`, `EmailChangeRevertLinkFactory` — scheme
  and host from the configured issuer, never the `Host` header (copied from
  `EmailVerificationLinkFactory`).
- **`Pages/Account/ConfirmEmailChange.cshtml(.cs)`** — `?userId=&email=&token=` on the GET, the
  same values posted back from a hidden form. `[EnableRateLimiting("auth-endpoint-limit")]`, like
  every sibling page.
- **`Pages/Account/RevertEmailChange.cshtml(.cs)`** — `?userId=&from=&to=&token=`, same shape. The
  POST refuses unless the account currently sits on `to`, restores `from`, nulls `PasswordHash`,
  rotates the stamp, revokes sessions and hands the visitor a password-reset token, exactly as
  `CancelAccountDeletion` does.
- **`ApiErrors/AuthErrors`** += `InvalidEmailChangeToken`, `EmailChangeSameAddress`,
  `EmailChangeFailed(details)`. `UserAlreadyExistsByEmail`, `InvalidCurrentPassword` and
  `DeletionAlreadyScheduled` are reused.
- **Rate limiting** — a new `change-email-limit` policy in `Program.cs`: **3 per hour partitioned
  by remote IP**, copying `forgot-password-limit`, which guards the same abuse (flooding a
  stranger's inbox). It cannot be partitioned by user: `app.UseRateLimiter()` sits at
  `Program.cs:380`, deliberately **before** `app.UseAuthentication()` at `:383`, so
  `httpContext.User` is still the anonymous default when the partition key is computed — a
  "user id" key would collapse to one global bucket any anonymous caller could exhaust.
- **Contracts** — `Features/Auth/Account/ChangeEmailRequest.cs`
  (`string NewEmail, string CurrentPassword`), and `Rels.ChangeEmail = "change-email"` advertised
  by `AccountAggregateLinkFactory` for an active (non-deletion-scheduled) account.

### Frontend (`LotroKoniecDev.Frontend`)

- **`Components/Pages/Account/ChangeEmail.razor`** — `/account/change-email`, `[Authorize]`,
  static SSR, mirroring `ChangePassword.razor`: new address + repeat + current password, follows
  the `change-email` rel from the account export envelope, renders API `ProblemDetails`, and on
  success shows "sprawdź nową skrzynkę" instead of claiming the address already changed. The copy
  states plainly that the address is also the login, and that the old mailbox gets a way to undo.
- **`AccountLoader.RequestEmailChangeAsync(href, newEmail, currentPassword, ct)`**.
- **`Account.razor`** — a "Zmień e-mail" action row in "Operacje", gated on
  `Links.HasLink(Rels.ChangeEmail)`, plus a "Zmień" link next to the e-mail value in "Dane konta".

### Docs & legal copy

- `Pages/Account/PrivacyPolicy.cshtml` — the "Realizacja praw bez formalności" card names only
  export and deletion today; it gains the e-mail change (owner decision). §06's rectification
  promise needs no edit — this ticket is what makes it true.
- `docs/API.md` — the auth endpoint list and the account-aggregate rel table both need the new
  entries.

### Tests

- `AuthSystem.API.Tests.Unit` — the `RequestEmailChange` handler across every branch; the confirm
  and revert handlers; both processors, including repeat-safety and the stale-request skip; both
  token providers, including the stamp-rotation assertion that is ADR-0048 made executable.
- `AuthSystem.API.Tests.Integration` — the endpoint (401 / 400 / 422 / happy), the confirm page
  (GET mutates nothing, valid POST, tampered address, expired, replayed, address taken meanwhile)
  and the revert page (GET mutates nothing, valid POST, wrong current address, after a password
  change, replayed). The 429 case needs its own factory with `RateLimiting:ForceEnable`, mirroring
  `Tests/RateLimiting/ResendConfirmationRateLimitingTests` — the limiter is off in Testing.
- `Frontend.Tests.Unit` — loader + bUnit render tests, mirroring the `ChangePassword.razor` tests.

## Out of scope

- **Recovering an account whose registration address was mistyped** (owner decision). With
  `RequireConfirmedEmail = true` such a user cannot log in, so an authorized endpoint can never
  reach them. Fixing that needs an anonymous, password-verified flow with a different threat model
  — a separate ticket.
- **Blocking the attacker during the revert window.** ADR-0048 accepts that the account is really
  taken over for up to 14 days; a lockout would break the users the feature exists for.
- **Changing the username.** `UserName` is separate from `Email` here (`RegisterUser` takes both),
  and nothing in the ticket asks for it.
- **A `PendingEmail` column or an audit table.** The pending change itself lives in the token. The
  schema change is two nullable columns, `EmailChangeRevertStamp` and `EmailChangeRevertTo`
  (additive, expand-only — ADR-0023's cheapest shape), which are what make a revert link both
  single-use and impossible to point somewhere the owner did not start from. The audit trail the
  ticket asks for is structured logging with masked addresses + IP + user agent, which is what
  `DeleteAccount` and `CancelAccountDeletion` already do.
- **Two-factor / step-up auth** on the change. The password is the factor the product has.
- **Security headers on the auth origin.** The CSP of bug #670 is emitted only by the frontend's
  `SecurityHeadersMiddleware`; the auth origin ships none, so the new pages' inline `<style>` is
  unaffected. That the auth origin has no security headers at all is a real pre-existing gap and a
  separate ticket.

## Business rules & edge cases

- **The address moves only at confirmation.** Until the form in the new mailbox's link is
  submitted, the account keeps the old address and the old login. An unconfirmed request expires.
- **`RequireConfirmedEmail = true`** (`PersistenceDependencyInjection.cs:50`) makes the single-write
  confirm mandatory: address and `EmailConfirmed = true` must land in one `Store.UpdateAsync`, or a
  crash between two writes locks the user out of their own account. Revert obeys the same rule.
- **Uniqueness is checked twice, and neither check is airtight.** `UserManager.UpdateAsync`
  validates with an application-level `FindByEmailAsync` *before* the write, so it is TOCTOU; the
  `UniqueEmailIndex` migration is the real arbiter. `UserStore.UpdateAsync` catches only
  `DbUpdateConcurrencyException`, so a Postgres `23505` escapes as `DbUpdateException`. Both write
  legs wrap the save in `RegisterUser`'s
  `catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)` and return a
  `Result` failure — never a 500 on a page a user reached from an e-mail.
- **Do not set `NormalizedEmail` by hand.** `UpdateAsync` recomputes it after validation and before
  the write, so a hand-set value is dead code.
- **The revert can find its old address taken.** Between the change and the click, somebody may
  have registered the freed address. The revert then fails with the generic invalid-link state; the
  account stays where it is and the password is **not** nulled.
- **"Already in use" is said out loud.** `RegisterUser.RegisterAsync` already returns
  `UserAlreadyExistsByEmail` to an *anonymous* caller, so telling an authenticated,
  password-verified, rate-limited caller the same thing adds no exposure the product does not
  already accept.
- **A scheduled deletion blocks the change**, for ADR-0031's reason: the account is locked and the
  emailed cancel link is the only way back in; a stamp rotation here would break it.
- **Confirming and reverting both rotate the security stamp**, which makes each link single-use,
  ends every cookie session via `SecurityStampCookieValidator` and — with `IUserSessionRevoker` —
  revokes the OpenIddict tokens and authorizations. Same treatment as `ChangePassword`.
- **A password change invalidates a pending e-mail change** (same stamp) but **not** a revert link
  (ADR-0048 — that is the point). The user simply requests the change again.
- **Only the first change since the last revert arms an undo, and it arms
  `ApplicationUser.EmailChangeRevertTo` at the address the chain started from.** A later change in
  the same chain sends its notices without a link. A revert restores that armed address — never the
  one its link names — and clears it, rotating `EmailChangeRevertStamp` so every issued token dies.
  All three parts are load-bearing; ADR-0048 records the two shapes that looked sufficient and were
  each defeated.
- **The revert cancels a scheduled deletion too.** Rotating the security stamp kills ADR-0031's
  cancel token, and after an address change that cancel link went to the address the account was
  moved to. Refusing instead would leave the account locked, unable to log in or reset, and erased
  by the finalizer.
- **Requesting a second change does not kill the first link.** Each token is bound to its own
  target address, so two outstanding links can only ever land on the address their own e-mail was
  sent to. Whichever is confirmed first wins; the other dies with the stamp rotation.
- **The links are tamper-evident, and their values are checked for shape first.** Addresses travel
  in the query string but are baked into the token purpose, so editing them fails verification. The
  `userId` must parse as a `Guid` before it reaches Identity, which converts it to the key type and
  throws on anything else, and the addresses must match `EmailConstants.RegexPattern` before the
  page prints them — otherwise both pages become a place to put arbitrary text next to a real
  button.
- **The old mailbox is told the full new address**, in both the warning and the notice. The only
  recipient is whoever controls the old mailbox: the legitimate owner, who needs it to act.
- **A stale request warning is dropped, not sent.** If `EmailChangeRequested` is delivered after
  the change already completed, `user.Email` no longer equals its `CurrentEmail`; the processor
  acknowledges and sends nothing, so the warning can never reach the new (possibly attacker's)
  mailbox.
- **Case and whitespace.** Addresses are trimmed before use, like `LoginModel` does, and compared
  through `UserManager.NormalizeEmail`, so `Jan@X.pl` is "the same address" as `jan@x.pl`.
- **At-least-once delivery can double a send.** `EmailDeliveryProcessor` writes the inbox row only
  *after* `ProcessAsync` succeeds, so a failure on the second of two e-mails resends both. A
  duplicated warning and a resent (identical) link are harmless — the bar ADR-0038's registry sets.
- **The TMS translator profile follows automatically.** `Translator.Email` is a `ComplexProperty`
  with no unique index, and `TranslatorProvisioner` refreshes it whenever the claims fingerprint
  moves — so the new address propagates on the next authenticated TMS request with no work here.
  `Translation.SubmittedById` is an `IdentityId`, never an address.

## Contract

- **Trigger:** `POST auth/account/change-email` (authorized), then
  `GET`+`POST /Account/ConfirmEmailChange` (anonymous, token-authorized), then optionally
  `GET`+`POST /Account/RevertEmailChange` (anonymous, token-authorized).
- **Input:** `ChangeEmailRequest(string NewEmail, string CurrentPassword)` →
  `RequestEmailChange.Command(UserId, NewEmail, CurrentPassword, IpAddress, UserAgent)`.
- **Output:** `200 OK` with no body on the request leg, mirroring its nearest sibling
  `ChangePassword` (`DeleteAccount`'s 204 carries headers this endpoint has none of); an HTML
  form, then a success or error state, on the two page legs.
- **Errors:** `401` no subject claim · `400` validation ·
  `400 Auth.InvalidCurrentPassword` · `400 Auth.EmailChangeSameAddress` ·
  `422 Auth.UserAlreadyExistsByEmail` · `422 Auth.DeletionAlreadyScheduled` · `429` rate limit.
  (`ErrorExtensions` maps `Validation` → 400 and `DataConflict` → 422 repo-wide.)
  Both pages render `Auth.InvalidEmailChangeToken` as "link wygasł lub jest nieprawidłowy".
- **Files touched:** no DAT and no translation artifact. One EF migration,
  `AddEmailChangeRevertFieldsToUsers` — two nullable columns, additive and N-1 safe.

## Acceptance criteria

- [ ] A signed-in user sees a "Zmień e-mail" entry in "Moje konto" whenever the account export
      envelope advertises the `change-email` rel, and does not see it while a deletion is scheduled.
- [ ] `POST auth/account/change-email` with a wrong current password returns
      `Auth.InvalidCurrentPassword` and enqueues nothing.
- [ ] A request for an address another account holds returns `Auth.UserAlreadyExistsByEmail`;
      a request for the caller's own current address returns `Auth.EmailChangeSameAddress`.
- [ ] A valid request **commits** exactly one `EmailChangeRequested` row (asserted against the DB,
      not against the writer), and its processor sends the verification link to the **new** address
      and a warning naming that address to the **old** one.
- [ ] The account's e-mail is unchanged until the confirmation is submitted; the user can still log
      in with the old address in the meantime.
- [ ] **A `GET` of either the confirm or the revert URL changes nothing** — it only renders a form.
      This is the mail-scanner guard and it is asserted directly.
- [ ] Submitting the confirmation sets the new address with `EmailConfirmed = true`, rotates the
      security stamp, revokes the user's sessions, and commits one `EmailChangeCompleted` row in the
      same save as the address change.
- [ ] `EmailChangeCompleted` produces a notice to the new address and a notice **carrying the
      revert link** to the previous one.
- [ ] Submitting the confirmation a second time fails with the invalid/expired state and changes
      nothing; editing its `email` query parameter fails verification and changes nothing.
- [ ] The revert restores the previous address, sets `PasswordHash = null`, rotates the stamp and
      sends the visitor into the password-reset flow.
- [ ] **The revert link still works after the password has been changed** — the stamp rotation does
      not invalidate it (ADR-0048).
- [ ] The revert refuses when the account is already back on the armed address, when nothing is
      armed, and when the armed address has been taken meanwhile — the last without nulling the
      password.
- [ ] A uniqueness race on either write leg renders the error state, not a 500.
- [ ] After the change the user logs in with the new address, and the old address is rejected.
- [ ] The 4th request within an hour is answered `429` (own factory, `RateLimiting:ForceEnable`).
- [ ] Every form works with JavaScript disabled (`scripts/check-ssr-purity.sh` stays green) and
      `scripts/check-client-hypermedia.sh` stays green — the frontend resolves the endpoint by rel.
- [ ] The privacy policy's self-service card names the e-mail change, and `docs/API.md` lists the
      new endpoint and rel.
- [ ] A revert token minted before an earlier successful revert is refused, so an attacker who
      chained a second change cannot undo the owner's recovery with the link sent to their mailbox.
- [ ] A revert on an account with a scheduled deletion cancels the deletion instead of leaving it
      to be erased.
- [ ] A malformed `userId` or address in either link renders the error state, never a 500, and is
      never printed on the page.
- [ ] `dotnet build` green with zero warnings; unit + integration suites green.

## Open questions

**Empirical — answered from the code (buddy pass, 2026-08-20):**

- *Is the e-mail the login identifier?* Yes — `Pages/Account/Login.cshtml.cs` resolves the user
  with `FindByEmailAsync(Email.Trim())`. The UI copy must say the login changes with it.
- *Does `CancelDeletion` mutate on `GET`?* **No** — `CancelDeletion.cshtml.cs:11-15` renders a form
  precisely so mail scanners cancel nothing. An earlier draft claimed the opposite and would have
  shipped a self-triggering revert link.
- *Can a rate-limit partition read the authenticated user?* **No** — `UseRateLimiter` at
  `Program.cs:380` runs before `UseAuthentication` at `:383`, deliberately.
- *Does `Enqueue` alone commit?* **No** — `OutboxWriter.Enqueue` only calls `Add`; a handler that
  mutates nothing must save explicitly, as `ForgotPassword` does.
- *Are Identity's `GenerateChangeEmailTokenAsync` / `ChangeEmailAsync` public, and does the latter
  save twice?* Public, and it saves **once**. The custom provider is justified by verify-before-
  enqueue only.
- *Would a `DataProtectorTokenProvider` revert token survive the attacker's password change?* No —
  it embeds the security stamp (the store implements `IUserSecurityStampStore`) and rejects the
  token once the stamp moves. Hence the hand-written revert provider.
- *Does the repo accept e-mail enumeration on an authenticated, password-gated endpoint?*
  `RegisterUser.RegisterAsync` already returns `UserAlreadyExistsByEmail` to an anonymous caller.
- *Does a new routing key need a broker topology change?* No — `EmailQueue` binds `email.#` and
  queue arguments are untouched (ADR-0036).
- *Is a DB migration needed?* One, for the two revert fields — the pending change itself is still
  stateless. Three review passes proved a stateless guard cannot exist here: the owner's token and
  the attacker's are structurally identical, so only server state can say which address is a legal
  destination and which links are spent. The TMS
  side needs nothing (no unique index on `Translator.Email`).
- *Does #670's CSP affect the new auth pages?* No — the CSP comes only from the frontend's
  `SecurityHeadersMiddleware`; the auth origin emits none.

**Business decisions — answered by the owner, 2026-08-20:**

- **Guard level** → the old mailbox gets a 14-day single-use revert link that also destroys the
  password; the old mailbox does **not** gate the change. Recorded as ADR-0048.
- **Old-address warning at request time** → yes, in addition to the notice after completion.
- **Verification link lifetime** → 24 h, matching the activation link.
- **The mistyped-registration-address lockout** → out of scope for LEGAL-14.
- **Privacy policy** → extend the "Realizacja praw bez formalności" card to name the e-mail change.

## Assumptions

- The e-mail pipeline (outbox → relay → broker → processor) is the delivery mechanism for both new
  message types; no direct SMTP call on a request path (ADR-0038; `ResendEmailConfirmation` stays
  the single deliberate exception).
- Polish copy for every user-facing string lives in the frontend and in the auth Razor pages
  (ADR-0044); the API returns `Error` codes, not sentences for the end user.
- No real users yet, so the new rel and the new endpoints ship without any back-compat shim.
