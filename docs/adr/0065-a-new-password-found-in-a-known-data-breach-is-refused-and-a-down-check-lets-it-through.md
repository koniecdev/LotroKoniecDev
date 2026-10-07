# ADR-0065: A New Password Found in a Known Data Breach Is Refused, and a Check That Cannot Run Lets It Through

**Status:** Accepted
**Date:** 2026-10-07
**Decision-makers:** Solo maintainer (ticket #694)
**Related:** AuthSystem.Infrastructure (`PwnedPasswords/`), AuthSystem.API (`Services/Accounts/BreachedPasswordValidator.cs`,
`Features/Auth/{RegisterUser,ChangePassword,ResetPassword}`, `Pages/Account/{Register,ResetPassword}`),
Frontend (`Infrastructure/Errors/ApiProblemCopy.cs`); ADR-0044 (the Frontend's Polish error copy),
ADR-0053 (the password-confirmation budget), ADR-0059 (the time floor on the reset answer); tickets
#694, #1045, #1046

## Context

Most account takeovers start with a password the attacker already has. It is rarely guessed. It is
reused from another service that leaked it, and it sits in every credential-stuffing list. Our
password policy (8 characters, a digit, a lower and an upper letter, a special character) accepts
`Password1!`, which Have I Been Pwned has seen more than half a million times.

Have I Been Pwned runs a free **Pwned Passwords range API**. We checked its terms at the source
(haveibeenpwned.com/API/v3) on 2026-10-07:

- `GET https://api.pwnedpasswords.com/range/{first 5 hex characters of the SHA-1}`.
- No API key, no rate limit, no licence or attribution requirement.
- The answer lists every hash suffix in that bucket with a breach count, one `SUFFIX:COUNT` per line.
- With the `Add-Padding: true` header, every answer is padded with fake lines that have a count of 0,
  so the size of the answer does not tell which bucket was asked for.

The client sends only the five-character prefix and compares the rest of the hash itself. Each bucket
holds about two thousand real hashes, so the service cannot tell which one we asked about, and the
password never leaves our process (k-anonymity). The breach search by e-mail address is a different,
paid API, and we do not use it.

A password is set in four places: registration, password change, password reset, and the admin seed
(Development and Testing only, ADR-0056). The reset has two front ends, the auth server's Razor page
and the API endpoint; the page calls `UserManager.ResetPasswordAsync` directly, not the handler.

## Decision

### 1. The check is an Identity password validator

`BreachedPasswordValidator` implements `IPasswordValidator<ApplicationUser>`. Identity runs every
registered password validator inside `CreateAsync(user, password)`, `ChangePasswordAsync` and
`ResetPasswordAsync`, so all four places above are covered by one registration, and a fifth place
added later is covered too. Identity checks the current password (change) and the token (reset)
before it validates the new password, so a wrong current password or a dead link never causes a call.

`AddIdentityCore` registers the built-in validator with `TryAdd`. Ours is registered after it, in
`AddAuthApi`, so it joins the built-in rules instead of replacing them.

The policy rules come first. A password that breaks them is refused by the built-in validator, whose
error names the rule, so ours does not ask the service about it at all. Nearly every weak password is
in a breach list too, and the reset page would otherwise show the vaguer breach message instead of
the rule. The handlers and the reset page map a result to the breach message only when the breach is
its one error.

### 2. Only the five-character prefix leaves the process

`PwnedPasswordChecker` hashes the password with SHA-1 (UTF-8), sends the first five hex characters to
the range endpoint with `Add-Padding: true`, and looks for the other 35 in the answer. A matching line
counts only with a count above zero, because padding lines carry 0. The answer is read as bytes and
decoded as UTF-8, whatever charset it names (#1036), and a byte order mark in front is dropped.

A real answer always holds hundreds of `SUFFIX:COUNT` lines. A successful answer with none, such as a
proxy's error page, says nothing about the password, so it counts as `Unavailable`, not as clean.

The prefix is safe at Have I Been Pwned, which cannot tie it to anyone. In our own logs and traces it
would sit next to the request and the account it belongs to, and 20 bits of an unsalted SHA-1 cut a
cracking dictionary about a million times. Nothing else may travel with it either. So:

- The client has no request logging (`RemoveAllLoggers`). The factory's own handlers would log every
  URL at Information.
- Its handler has no activity propagator (`ActivityHeadersPropagator = null`). By default .NET adds
  `traceparent` and `baggage` to every request, which would hand Have I Been Pwned the trace id our
  own logs carry next to the user. Without a propagator the runtime adds no trace handler at all, so
  no header goes out and no span records the URL. A probe on .NET 10 with the OpenTelemetry SDK
  showed both before the change and neither after. `RangeApiRequestTests` pins it with the real
  handler. The HTTP metrics carry the host, never the path.

### 3. The client is narrow

It is its own typed `HttpClient`: a fixed base address, a 3-second timeout, no redirects, no cookies,
a 1 MB cap on the answer (a padded bucket is about 80 KB today), and no retry. A retry only makes the
person on the form wait longer for an answer the policy can do without.

### 4. Fail-open: a check that cannot run lets the password through

When the service is down, slow, answers with anything but success, or answers with no hash line, the
verdict is `Unavailable`. The validator accepts the password and the checker logs a warning (event ids
3300–3303). The rest of the password policy still applies. A refusal is logged too, at Information
(event id 2740), so an operator can tell a check that works from one that never runs.

The call stops when the visitor's request is aborted (`HttpContext.RequestAborted`), so a visitor who
gave up does not keep a registration's transaction open for the whole time limit. The admin seed runs
outside a request and waits.

A fail-closed check would make registration, password change and password reset depend on the uptime
of a service we do not run. During an outage of Have I Been Pwned or of the network path to it, nobody
could register or recover an account. A breached password slipping through during an outage is the
smaller harm, and the warning makes the outage visible.

### 5. Login is never checked

Login sets no password, so the validator never runs there. A password that appears in a leak after it
was set does not lock its owner out. A prompt that asks such a user to change the password is parked:
it needs the password in plain text, which exists only during login, and where the prompt lives is a
product decision. It is #1045.

### 6. A real answer is remembered for five minutes

The verdict is kept in the process memory cache for five minutes, keyed by the full SHA-1 hash, so a
form posted twice, or the same password typed again after an error, asks only once. `Unavailable` is
never kept, so the next attempt tries again. The key is the hash, not the password, and it never
leaves the process.

### 7. The refusal has its own error code and Polish copy

The handlers map the validator's error to `Auth.PasswordFoundInBreaches` (400). The register page, the
reset page and the Frontend's error map all show the same sentence: "To hasło pojawiło się w wyciekach
danych, wybierz inne." A refused reset keeps the form, because the link is still good.

## Consequences

### Positive

- `Password1!` and every other known-breached password can no longer be set anywhere.
- One validator covers every path that sets a password, including paths added later.
- No secret, key or account is needed. Only the first five hex characters of the password's SHA-1
  leave the process, only to the range API, and they are kept out of our own logs and traces.

### Negative / Accepted Trade-offs

- **A new outbound dependency.** The auth API now calls `api.pwnedpasswords.com` over HTTPS. A box that
  blocks outbound traffic turns the check off in practice; the warnings in the log are the signal.
- **An outage lets breached passwords through.** Accepted, see decision 4.
- **Registration holds its database transaction during the call.** The validator runs inside
  `CreateAsync`, after the address and username lookups. At worst that is 3 seconds with one pooled
  connection and no row locks. Registration is rate limited.
- **A refused new password on the password change spends a confirmation permit.** The current password
  was checked first, and ADR-0053 §2 spends a permit on every attempt, a correct password included. A
  person who picks ten breached passwords in a quarter of an hour waits fifteen minutes.
- **SHA-1 appears in the code.** It is what the range API is keyed by. It is never stored and never used
  to verify a password.
- **The admin seed is checked too.** In Development, an `AdminUser:Password` from a breach list now stops
  startup with a clear error. Deployed boxes seed the admin without a password (ADR-0056).
- **The browser E2E suites call the real service.** Their passwords were checked clean on 2026-10-07,
  and an offline run passes by decision 4. The integration suite replaces the checker with a stub, so it
  never reaches the internet.
- **Verdicts sit in memory for five minutes.** A heap dump would show the SHA-1 of recently checked
  passwords. Whoever can take a heap dump of the auth server already holds its signing keys. The cache
  has no size limit: one small entry per distinct password, and every path that reaches it is rate
  limited.

## Alternatives Considered

### A. Fail-closed

Refuse every new password while the service cannot answer. Rejected: it turns an outage of a free
third-party service into our outage of registration and account recovery.

### B. Call the checker from each handler and page

Rejected: four call sites today, and the reset page and the admin seed already bypass the handlers. A
path added later would silently skip the check. The Identity validator is the one seam every path goes
through.

### C. Download the breach corpus and check locally

No third party at all, but the corpus is tens of gigabytes and needs a refresh job. Rejected for a
two-box deployment with no real users yet.

### D. Check at login too

Rejected by the ticket: it would lock out a user whose password leaked after they set it. A prompt to
change it is the right shape, and it is parked in #1045 (decision 5).

### E. A configuration switch to turn the check off

Rejected: no present need, and a switch that silently turns off a security check is a risk of its own.
Tests replace the checker in the container instead.

## Implementation Notes

- `IPwnedPasswordChecker`, `PwnedPasswordVerdict`, `PwnedPasswordChecker` and
  `PwnedPasswordsDependencyInjection` live in `AuthSystem.Infrastructure/PwnedPasswords/`.
  `BreachedPasswordValidator` lives in `AuthSystem.API/Services/Accounts/`, because it needs
  `ApplicationUser`, and `IdentityResult.IsBreachedPassword` maps its error in the handlers and the
  reset page.
- Tests: `PwnedPasswordCheckerTests` (what is sent, padding lines, unreadable answers, errors, timeout,
  the cache), `PwnedPasswordsDependencyInjectionTests` (the registered client, its time limit, its
  handler and its silent logging) and `BreachedPasswordValidatorTests` (unit); `RangeApiRequestTests`
  (the real handler against a loopback listener: no trace or baggage header), the register,
  change-password and reset endpoint and page tests, the admin seed, and a login page test that a
  password breached after it was set still signs in (integration, with `StubPwnedPasswordChecker`).

## References

- #694 — the ticket.
- #1045 — the parked prompt for a password that leaks after it was set.
- #1046 — the reset page skips the API's 128-character maximum (older than this ADR).
- https://haveibeenpwned.com/API/v3#PwnedPasswords — the range API, its padding and its terms.
- ADR-0056 — the admin is seeded without a password on deployed boxes.
- #1036 — an unknown charset in an answer must not throw.
