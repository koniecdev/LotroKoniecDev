# ADR-0053: Password Confirmation Is Braked per Account, Not by Identity's Lockout

**Status:** Accepted
**Date:** 2026-09-22
**Decision-makers:** Solo maintainer (ticket #813)
**Related:** AuthSystem.API (`Features/Auth`, `Services/RateLimiting`), Frontend HTTP resilience,
ADR-0031 (deletion grace period locks the account through `LockoutEnd`), ADR-0046 (the rate-limit
lesson on the auth pages), ADR-0052 (the export step-up, which surfaced this), tickets #690, #692,
#813

## Context

Four endpoints ask a logged-in caller for the current password: `POST auth/account/delete`,
`POST auth/change-password`, `POST auth/account/change-email` and `POST auth/account/data-export`
(#690). The password is what turns a stolen session or a still-valid token into deletion, an
address change, a new password or the GDPR file. Code facts that shaped this decision:

- None of the four touched Identity's lockout counter. `CheckPasswordAsync` and
  `ChangePasswordAsync` compare hashes and nothing else; `AccessFailedAsync` is called only on the
  login form and the token endpoint (`Login.cshtml.cs`, `TokenEndpoint.cs`).
- The only brake was a per-IP policy: `auth-endpoint-limit` (10 per minute) on three of them,
  `change-email-limit` (3 per hour) on the fourth, both keyed on `Connection.RemoteIpAddress`
  (`Program.cs`). Every one of these calls is made by the frontend, server to server, through Caddy.
  The auth API trusts Caddy alone with `ForwardLimit = 1`, and the frontend forwards no client
  address, so the auth API sees the frontend container's address for every user. One bucket, shared
  by everyone: a session thief gets 10 guesses a minute forever, and one user's traffic can 429
  another user's password change. The same fact means "key the policy on the user" is impossible:
  `UseRateLimiter` runs before `UseAuthentication` on purpose (OpenIddict's `/connect/*` checks), so
  a policy never sees a principal.
- A per-account budget already exists for one endpoint: `PasswordResetRequestThrottle` (#692), an
  in-process `PartitionedRateLimiter<Guid>` keyed on the account id, taken inside the handler where
  the account is known.
- `LockoutEnd` is not only the login lockout. ADR-0031 locks a deletion-scheduled account for the
  whole 14-day grace period by setting `LockoutEnd = finalizesAt` (`DeleteAccount.cs`).
  `UserManager.AccessFailedAsync` overwrites `LockoutEnd` with "now plus five minutes" once the
  counter reaches the limit.
- The frontend's HTTP pipeline retries a request up to twice on any exception, the ten-second
  per-attempt timeout included (`HttpClientsDependencyInjectionExtensions.cs`). A slow auth API
  still serves the first attempt.

## Decision

### 1. One per-account budget, shared by every endpoint that confirms the password

`PasswordConfirmationThrottle` is a singleton `PartitionedRateLimiter<Guid>` keyed on the account
id from the token — never on an address, and never on the address text, for the reason #692 gives
(`NormalizeEmail` folds two spellings of a Polish address into one account; an id cannot be spelled
two ways). Its budget is **10 confirmations per 15 minutes per account**: the room the login form
already gives a client for wrong passwords (`auth-page-limit`), enough for a few typos and every
sensitive action in a row, far too little to spray with. A guesser picks whichever endpoint is open,
so one budget across all four is what makes it a brake on guessing rather than on one form.

### 2. The permit is taken before the password is checked, so every attempt spends one

The handler takes the permit, then checks. A budget that counted only failures would have to check
first and charge afterwards, and a burst of concurrent guesses would then all pass the gate before
the first failure is recorded. A fixed window cannot hand a permit back once the check succeeds, so
the atomic order is the only safe one, and a correct password spends a permit too. A person confirms
a password a handful of times a day and never notices; a script hits the limit in seconds. The
permit sits immediately before the password check in each handler: after validation, after the
account lookup, and in `DeleteAccount` before the deletion-scheduled refusal, which only a caller
who proved the password gets to see.

### 3. Identity's lockout stays out of it

A failed confirmation does **not** feed `AccessFailedAsync`, for two reasons:

- The caller here is already inside the session. On the login form the caller is anonymous and
  locking them out is the point; here, the only thing a lockout adds is a way for a session thief
  to lock the real owner out of login with five wrong guesses.
- `AccessFailedAsync` overwrites `LockoutEnd`. Fed from these endpoints, five guesses against a
  deletion-scheduled account would shorten its 14-day lock to five minutes. The export is served
  during that window on purpose (ADR-0052), so the path exists.

The per-account budget bounds guessing on its own, at a rate below what the login form allows.

### 4. The refusal is a 429 with its own error code

`AuthErrors.PasswordConfirmationThrottled` carries the new `TypeOfError.TooManyRequests`, which both
APIs' `ErrorExtensions` map to 429 (the two files are kept identical on purpose). The frontend
already treats a 429 from these calls as "too many attempts": the export route turns it into its
`throttled` marker, and the other three pages read the new code's Polish sentence from
`ApiProblemCopy`. No `Retry-After` header: the window is fixed and documented, and no client reads
it.

### 5. The three password endpoints leave the per-IP policies; change-email keeps its mail budget

`DeleteAccount`, `ChangePassword` and `DownloadAccountData` call `DisableRateLimiting()`. An IP key
on a back-channel endpoint is one bucket for every user, so it was never a brake on guessing and it
was the reason one user's page views could refuse another user's password change — the ticket's
second acceptance criterion. The per-account budget is their brake now. An anonymous flood costs
what any 401 costs (a signature check), and a token holder past the budget costs one primary-key
lookup per refused request, which is the price of logging the refusal against the account.
`RequestEmailChange` stays on `change-email-limit`: that policy bounds the **mail** the endpoint
sends to an address the caller typed, which the confirmation budget does not replace.

### 6. The frontend sends a password confirmation exactly once

The four request contracts carry `IPasswordConfirmationRequest`, and the resilience pipeline's
`MayRetry` refuses to retry a request whose body implements it, the same way it already refuses a
multipart upload. Without this, one click on a cold backend could spend two or three permits while
the auth API served every attempt.

## Consequences

### Positive

- Guessing the current password from inside a session is bounded per account: 10 attempts per
  quarter of an hour, on whichever endpoint, from however many addresses.
- One user's traffic can no longer refuse another user's deletion, password change or export.
- The deletion grace lock of ADR-0031 cannot be shortened by guessing.

### Negative / Accepted Trade-offs

- **A session thief can burn the owner's budget** and deny them `change-password` and
  `delete-account` — the remediation actions — for up to 15 minutes. It is far milder than a
  five-minute login lockout, it clears itself, and the escape hatch stays open: a password reset
  from the login page goes through `ForgotPassword`, which has its own budget and rotates the
  security stamp, ending every session.
- **The budget is in process**, so two containers mean two budgets and a restart empties it. Every
  limiter in this app makes the same trade-off; "per account" does not mean "per account, globally".
- A correct password spends a permit. A person never reaches ten confirmations in a quarter of an
  hour; a QA run that exercises all four flows with a wrong-password case each spends eight.
- The remaining back-channel policies are still one bucket for every user: `auth-endpoint-limit`
  carries `/connect/token` and the account GET on every page view, and `change-email-limit` gives
  the whole product three e-mail changes per hour. That is the topology — the frontend forwards no
  client address — and it needs its own ticket, not a fourth endpoint-level exception.

## Alternatives Considered

### A. Feed `AccessFailedAsync` and let Identity's lockout do the braking

The standard shape, and the one the login form uses. Rejected. It hands anyone inside a session a
five-guess lockout of the real owner, and it overwrites the 14-day deletion lock (decision 3).

### B. Count failures after the check, so a correct password costs nothing

Rejected. Checking first and charging afterwards leaves a window in which concurrent guesses all
pass the gate; closing it needs a refund on success, which a fixed window cannot do. An in-house
counter with pre-charge and refund could, but it is a new mechanism where the sibling throttle
already exists, and the cost it avoids — a permit per correct password — is one nobody notices.

### C. Key the IP policy on the user

Impossible in this pipeline: the limiter runs before authentication on purpose, so the policy never
sees a principal. Parsing the bearer token unverified for its subject would make the key
attacker-chosen.

### D. Forward the real client address from the frontend and keep the IP policies

The right fix for the shared bucket as a whole, and out of scope here: it needs the frontend to
append `X-Forwarded-For`, Caddy to keep it, and the auth API to trust a second hop
(`ForwardLimit = 2`, the frontend's network in `KnownIPNetworks`) — a topology change with its own
security review. It also would not make the brake per account. Deferred to a follow-up ticket.

### E. Keep the three endpoints on `auth-endpoint-limit` next to the new budget

Rejected. The per-account budget would be met, but one user's page views could still refuse another
user's password change, which is the ticket's second acceptance criterion, and the IP policy added
nothing against guessing.

## Implementation Notes

- `Services/RateLimiting/IPasswordConfirmationThrottle.cs`, `PasswordConfirmationThrottle.cs`;
  registered as a singleton in `ApiDependencyInjection.cs`.
- `Features/Auth/DeleteAccount.cs`, `ChangePassword.cs`, `RequestEmailChange.cs`,
  `DownloadAccountData.cs`: the permit before the check; `EventIds.PasswordConfirmationThrottled`
  (2720) for the first three, the export's own audit line (2243) for the fourth.
- `ApiErrors/AuthErrors.PasswordConfirmationThrottled`; `TypeOfError.TooManyRequests` in the
  SharedKernel; the 429 arm in both `Extensions/ErrorExtensions.cs`.
- `AuthSystem.Contracts/Features/Auth/IPasswordConfirmationRequest.cs` on the four request records;
  `HttpClientsDependencyInjectionExtensions.MayRetry` in the Frontend; the Polish sentence in
  `ApiProblemCopy`.
- Tests: `PasswordConfirmationThrottleTests` (unit), `PasswordConfirmationBudgetTests`
  (integration, incl. the forced-on limiter proving the endpoints left the shared bucket),
  `HttpClientsResilienceTests` and `ApiProblemCopyTests` (frontend).

## References

- Ticket #813; #690 / ADR-0052 (where the gap surfaced); #692 (the per-account shape and the
  `NormalizeEmail` reasoning); ADR-0031 (the `LockoutEnd` grace lock); ADR-0046.
- `System.Threading.RateLimiting`: a `FixedWindowRateLimiter` partition reports no idle time while
  permits are spent, so the partitioned limiter never evicts a live budget early.
