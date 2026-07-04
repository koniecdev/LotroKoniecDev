# ADR-0022: Email is the login identifier; username is a display-only unique handle

**Status:** Accepted
**Date:** 2026-07-05
**Decision-makers:** Solo maintainer (product-owner decision 2026-07-04)
**Related:** ticket #333, TheKittySaver#256 (conceptual mirror, physically inverted), ADR-0004
(lazy translator provisioning — load-bearing for why the TMS needs no changes), `docs/INVARIANTS.md`
INV-11.2/11.5, QA #263

## Context

The product rule is: **authentication is e-mail + password**; the **username is a display-only
handle** that stays **unique across the system**, with **no spaces and no special characters**.
The implementation diverged on three points:

1. **Login was by username.** The hosted login page bound `Username` and resolved
   `FindByNameAsync`; the Testing-only password grant did the same.
2. **The username charset was an accident.** The only thing rejecting `"kasia 92"` was ASP.NET
   Identity's default `UserOptions.AllowedUserNameCharacters` — never configured, simultaneously
   too loose for the product rule (it admits `-._@+`, so `kasia.92`, `kasia_92`, even `kasia@92`
   registered happily) and surfacing as a raw English Identity error that the Polish register
   page mapped to a misleading "check your password" fallback.
3. **E-mail uniqueness was not physically case-insensitive.** `RequireUniqueEmail = true` is
   app-level; the unique indexes were on **raw** `Email`/`UserName`, while Identity's
   `EmailIndex` on `NormalizedEmail` was **non-unique**. A concurrent case-variant duplicate
   (`Foo@x.com` + `foo@x.com`) slips past both — `RegisterUser` already documents that exact
   race. With login moving to `FindByEmailAsync`, such a pair would throw
   ("Sequence contains more than one element") on the login path and permanently lock **both**
   accounts out — the switch turns a registration nuisance into a login outage.

TheKittySaver#256 solved the same product conversation with the opposite physical design: there,
the display name became non-unique free-form text, so Identity's `UserName` machinery (unique
index, normalization, charset) was wrong for it and `UserName` was repurposed to carry the email.

## Decision

### 1. `ApplicationUser.UserName` remains the username — the physical design inverts TheKittySaver#256

Here the handle **keeps** uniqueness and a restricted charset — exactly what Identity's
`UserName` natively provides (unique `UserNameIndex` on `NormalizedUserName`, case-insensitive
via normalization, `AllowedUserNameCharacters`). So there is no new column, no
`Username → DisplayName` rename, no claims rework: `Claims.Name` ← `user.UserName` stays correct
at all five issuance sites (authorize, token ×2, userinfo, login cookie), and the FE sidebar and
the TMS `TranslatorProvisioner` (ADR-0004) keep working untouched. The same product conversation
yields opposite physical designs in the two codebases because the *retained invariants* differ —
uniqueness + charset survive here, so the concept "username" survives with them.

### 2. The username charset is an explicit decision, enforced in three layers from one constant

`\A[a-zA-Z0-9]+\z` — ASCII letters + digits only; spaces, `-._@+` and diacritics all rejected;
`Gandalf`/`gandalf` collide via Identity normalization, as desired. The anchors are `\A`/`\z`,
not `^`/`$`, because .NET's `$` also matches before a trailing `\n` — `"kasia92\n"` would slip
through the validator and surface as the raw Identity error this ADR abolishes (same anchoring
style as `EmailConstants`). One source of truth —
`UsernameConstants` in `SharedKernel/Constants/` (next to `EmailConstants`): `MaxLength = 150`,
`AllowedCharacters`, `RegexPattern`. Three enforcement layers:

1. `RegisterUser.CommandValidator` — the authoritative rule; covers both public registration
   surfaces (hosted page and `POST auth/register`) with an explicit English message instead of
   raw Identity text.
2. The hosted Register page — a Polish pre-check next to the existing ones, so the misleading
   password-hint fallback is never reached for charset failures. UX-only duplication of the same
   constant; both layers read `UsernameConstants` so they cannot drift.
3. `options.User.AllowedUserNameCharacters = UsernameConstants.AllowedCharacters` — the
   last-resort Identity invariant; it also guards the seeder path (a mis-configured
   `AdminUser:Username` fails loudly at startup, by design).

### 3. Login is e-mail + password on every path

The hosted page binds `Email` and resolves `FindByEmailAsync`, with **every existing hardening
preserved verbatim**: dummy-hash timing equalization on not-found and lockout,
`AccessFailedAsync` on wrong password, the post-password `EmailConfirmed` gate (order matters —
no enumeration/timing oracle), security-stamp cookie claim, one generic error copy. The
user-not-found log line masks the attempted email (`MaskEmail()` — an email is PII; the old
username was not). The Testing-only password grant authenticates by email too: the OIDC
`username` wire parameter is a protocol constant and now semantically carries the email.

### 4. The identifier spaces are provably disjoint

The username charset excludes `@`; `EmailConstants.RegexPattern` requires `@`. An email typed
into the wrong lookup can never match, in either direction. Registration keeps both duplicate
pre-checks and both `DuplicateEmail`/`DuplicateUserName` race mappings — username uniqueness is
a feature here, not a bug.

### 5. `EmailIndex` on `NormalizedEmail` becomes UNIQUE

Configured in `ApplicationUserConfiguration` keeping the database name `EmailIndex` (otherwise
Identity's base mapping would create a second index), shipped as one Auth migration. This makes
the case-variant duplicate physically impossible at the storage layer — the login-outage
scenario above cannot occur. The raw-column unique indexes on `Email`/`UserName` are now
redundant next to the normalized pair but kept (harmless, and dropping them is orthogonal).

## Consequences

### Positive

- Login by e-mail matches the product rule and user expectations; the username stays a clean,
  URL-safe, screen-safe handle.
- Charset failures surface as explicit validator messages (English on the API, Polish on the
  page), never as raw Identity text behind a misleading password hint.
- Case-variant duplicate emails are impossible at the DB level, not merely improbable at the
  app level — the documented registration race loses its damage potential entirely.
- Zero TMS/Frontend changes: the claims contract is untouched, and ADR-0004's provisioner
  self-refreshes `DisplayName` if a username ever changes.

### Negative / Accepted trade-offs

- Deployed `AUTH_ADMIN_USERNAME` values containing `-`/`.`/`_` must be re-set to alphanumeric,
  or auth-api fails at startup (loud, by design; operator note in the runbook and PR).
- Diacritics are rejected in usernames — a deliberate product cut for a Polish-audience app
  (URL/display safety and zero homoglyph ambiguity outweigh naturalness of `kaśka92`).
- The OIDC password-grant wire key stays literally `username` while carrying an email — a
  protocol constant that now reads slightly dishonestly; locals/params are renamed for honesty,
  the wire key never.
- E-mail remains immutable post-registration; making it changeable while it is the login key
  needs its own ticket + ADR.

## Alternatives considered

### A. Mirror TheKittySaver#256 physically (UserName carries the email, new DisplayName column)

Rejected: the KittySaver design exists because its display name dropped uniqueness and charset
restrictions, making Identity's `UserName` machinery wrong for it. Here the handle keeps both —
repurposing `UserName` would discard exactly the native machinery (unique index, normalization,
charset enforcement) the product rule needs, then rebuild it by hand for a new column, and
ripple through claims issuance, the TMS provisioner and the FE.

### B. Unicode letter charset (`\p{L}\p{Nd}`) instead of ASCII

Rejected for now: `AllowedUserNameCharacters` is a flat char list (no regex), so Unicode classes
cannot be expressed in the third enforcement layer; and the PO rule says "no special characters"
with URL/display safety in mind. Revisit only on real user demand.

### C. Enforce charset only in Identity options (single layer)

Rejected: Identity failures surface post-`CreateAsync` as raw English error text — exactly the
misleading-UX bug this ticket removes. The validator is the user-facing rule; Identity options
are the invariant of last resort.

## Implementation notes

- `SharedKernel/Constants/UsernameConstants.cs` (new), `RegisterUser.CommandValidator`,
  `PersistenceDependencyInjection` (Identity options), `Login.cshtml(.cs)`,
  `Register.cshtml(.cs)`, `TokenEndpoint` (password grant), `ApplicationUserConfiguration` +
  one Auth migration (unique `EmailIndex`).
- HTML autofill semantics: the login email input keeps `autocomplete="username"` (the HTML spec
  token for the login identifier); the register username input becomes
  `autocomplete="nickname"`, its email input `autocomplete="username"`.
- `rg -n "FindByNameAsync" src/` must hit exactly two sites after this change: the RegisterUser
  duplicate pre-check and the seeder taken-username guard.

## References

- Ticket #333 — full change inventory and acceptance criteria
- TheKittySaver#256 — the conceptual mirror whose physical design this ADR deliberately inverts
- ADR-0004 — translator aggregate + lazy provisioning (why the TMS is untouched)
- `docs/INVARIANTS.md` §11 — INV-11.2 (charset + display-only), INV-11.5 (confirmation gate)
- WHATWG autofill guidance — `autocomplete` tokens for email-as-login
