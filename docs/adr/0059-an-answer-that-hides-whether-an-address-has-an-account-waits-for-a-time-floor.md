# ADR-0059: An Answer That Hides Whether an Address Has an Account Waits for a Time Floor

**Status:** Accepted
**Date:** 2026-09-24
**Decision-makers:** Solo maintainer (ticket #840)
**Related:** AuthSystem.API (`Services/ResponseTiming/`, `Pages/Account/{Login,ForgotPassword,ResetPassword,ConfirmEmail}`,
`Features/Auth/{ForgotPassword,ResendEmailConfirmation,ResetPassword,ConfirmEmail,CancelAccountDeletion}`);
ADR-0038 decision 5 (the "every path pays the same" rule this makes the second layer), ADR-0046 (the
login branches that need the password), ADR-0055 / ADR-0057 (the per-inbox mail budgets), ADR-0056
decision 4 (the password-less admin); tickets #314, #840, #860

## Context

Several anonymous pages and endpoints take an e-mail address and give the same answer whether or not
an account has it. The text is the same on every branch. The time was meant to be the same too, and
each of them does it the same way: when there is no account, it hashes a dummy password, so the
unknown address costs as much CPU as a real one. That idea holds for CPU. It fails for everything
else, and it has now failed three times in the same place: #314 (the lockout branch skipped the
hash), ADR-0056 decision 4 (a password-less account skipped it) and #840. Each fix covered the one
branch in front of it.

What the branches do today, besides the lookup:

- **Login page.** A wrong password for a real account calls `AccessFailedAsync`
  (`Login.cshtml.cs:144`, `:182`). That runs Identity's `UpdateUserAsync`: the user validator does
  `FindByNameAsync` and, because `RequireUniqueEmail` is on (`PersistenceDependencyInjection.cs:48`),
  `FindByEmailAsync`, and then the UPDATE commits. An unknown address (`:130`) and a locked-out one
  (`:163`) only hash. So a real account answers three database round trips slower.
- **Forgot password, page and API.** Every branch hashes. A real account that still has a send permit
  also inserts an outbox row and commits (`ForgotPassword.cshtml.cs:81`, `ForgotPassword.cs:103`).
- **Resend confirmation** (one handler behind the page and the API). An unknown address hashes
  (`ResendEmailConfirmation.cs:79`). A confirmed account (`:87`) and an unconfirmed one whose inbox
  budget is spent (`:98`) return with no hash, so they answer faster than an unknown one. An
  unconfirmed account with a permit waits for a live SMTP send (`:112`) — hundreds of milliseconds,
  and a direct send on purpose (ADR-0038 decision 4).
- **Reset password and confirm e-mail, page and API.** Only an unknown address hashes. A real account
  never does: with a wrong token it fails at Identity's token check, which unprotects the token and
  compares fields in memory; a deletion-scheduled account (reset) and a confirmed one (confirm) return
  before the token check. Either way a real account answers **faster** than an unknown one: the
  inverted oracle ADR-0038 decision 5 warned about. The token is a secret, but a wrong one is free to
  send.
- **Cancel account deletion** (one handler behind the page and the API). Both branches hash; a real
  account adds only the in-memory token check. There is no measurable gap here today.

Other facts that shaped the decision:

- Work can be matched for CPU, but not for a database write. A write has no free twin: a fake UPDATE
  does not take the same row lock, write the same WAL or wait for the same commit.
- The rate limits slow a measurement down but do not stop it. The login page allows 10 POSTs per 15
  minutes per address (an IPv6 client counts as its /64, ADR-0054); forgot-password and resend 3 per
  15 minutes; `auth-endpoint-limit` (the confirm-email page, the reset and confirm APIs) 10 per minute.
  Someone with many addresses gets many samples. Nothing limits concurrency, so they can also load
  the box: every one of these requests pays a PBKDF2 hash.
- **Some answers disclose the same fact openly, and a time floor cannot fix that:**
  - The confirm-email page answered "Twój adres e-mail został potwierdzony… Konto *x* jest aktywne"
    for any confirmed account, whatever the token (`ConfirmEmail.cshtml.cs:59-63`), and "the link is
    not valid" for an unknown address. No password, no token, no mail. Its API twin already answered
    "invalid token" there. Fixed here (decision 7).
  - Registration says "Konto z tym adresem e-mail już istnieje" (`Register.cshtml.cs:160`; the API
    allows 10 tries a minute per IP, the page 10 POSTs per 15 minutes). The e-mail change form says
    the same to any signed-in user, and a self-registered account is enough (`RequestEmailChange.cs:148-155`,
    3 per hour); its own comment justifies that by registration. Both are the owner's call and are
    tracked in #860.

  Until #860 is decided, the floor is defence in depth. It keeps the anonymous pages from adding a
  second, silent way to learn what registration already says, and it is in place when #860 closes
  the open disclosure.

## Decision

### 1. The answer leaves no sooner than a fixed time after the lookup starts

Every anonymous answer that must not reveal whether an address has an account waits until a fixed
floor has passed since just before the address lookup. When the branch finishes early, the request
waits for the rest. The members today:

| Where | Floor |
|---|---|
| Login page, every answer except the three that need the right password | 500 ms |
| Forgot password, page and `POST auth/forgot-password` | 500 ms |
| Reset password, page and `POST auth/reset-password` | 500 ms |
| Confirm e-mail, page and `POST auth/confirm-email` | 500 ms |
| Cancel account deletion (handler behind the page and `POST auth/account/cancel-deletion`) | 500 ms |
| Resend confirmation (handler behind the page and `POST auth/resend-email-confirmation`) | 3 s |

A new anonymous page or endpoint that looks an account up by a typed address and hides the result
joins this list in the same change.

### 2. Waiting is the default; only a proven secret skips it

The clock starts as the first step after input validation, before the lookup, and the wait runs in a
`finally`, so a branch that throws does not answer early either. A malformed address is refused
before the lookup and does not wait: that answer depends on the input alone.

- **Handlers** wrap their whole body after validation in one wait. Every branch pays it, including
  one added later. Reset, confirm and cancel also wait on success: success needs the mailed token, so
  it reveals nothing, and one rule is simpler than two. These actions are rare.
- **The login page** checks the credentials in one method that returns either an account whose
  password it verified, or nothing. Nothing waits. Only a verified account skips the wait, and only
  such an account reaches the success path and the two answers that name their reason (unconfirmed
  address, scheduled deletion — ADR-0046). A new failure branch inside that method waits without
  anyone remembering it. The success path is the hot path of the product and needs the password, so
  it does not wait.

### 3. 500 ms, and 3 s where a live mail is sent

500 ms is about two and a half times the slowest branch of a normal answer: one PBKDF2 hash (tens of
milliseconds on a box core) plus up to four round trips to the Neon database. Resend confirmation
holds a live SMTP send to the relay (a new connection, TLS, login and send each time), so its floor
is 3 s. The values are named constants in one class (`ResponseTimeFloors`), not configuration: a
setting would add a way to switch the guard off in production and buy nothing.

A floor hides a branch only while the branch finishes inside it. When the database stalls, the relay
is slow or down, or the CPU is saturated, a branch can run past the floor and the gap shows again. An
attacker can cause the last one by loading the box from many addresses. Decision 5 is what is left
then.

### 4. The wait spends no CPU and never ends early

The wait is `Task.Delay` on the injected `TimeProvider`, so a held request costs a timer, not a
thread or a hash. After each delay it checks the elapsed time again and waits for the rest: a timer
can fire a little early (up to about 15 ms on Windows), and the floor must be a lower bound. The wait
follows the request's cancellation token; a caller who hangs up learns nothing.

### 5. Every branch still pays exactly one hash

The equal-CPU rule of ADR-0038 decision 5 stays, as the second layer for an overrun floor, and now
holds on every branch: in forgot-password, resend, reset, confirm and cancel, the dummy hash runs
once on every path right after the lookup, as forgot-password and cancel already did. Resend's
confirmed and throttled branches, and reset's and confirm's real-account branches, gain the hash they
skipped. The login page keeps its per-branch hashes, pinned by `LoginPageTests`. The dummy hash goes
through `UserManager.PasswordHasher`, so the integration host's spy counts it.

### 6. One service, and a no-op floor in the integration suite

`IResponseTimeFloor` (singleton) starts a `ResponseTimer` for a given floor; the timer does the wait.
Every member uses it. The integration host replaces it with a floor that never waits, so the hundreds
of tests that post to these pages do not each pay 0.5–3 s. The floor tests put the production
registration back and check, per branch, that the answer takes at least the floor.

### 7. The confirm-email page names success only for a valid token

For an account that is already confirmed, the page now checks the token as well. A valid token (the
owner clicked the mailed link twice) still shows the success page; any other token gets the same
"link not valid" answer as an unknown address. The token stays valid after confirmation, because
confirming does not change the security stamp, so the double click keeps working for the link's 24
hours.

## Consequences

### Positive

- The time of these answers no longer depends on what a branch does, only on the floor. That covers
  the database write on login, the outbox row on forgot-password, the live mail on resend and the
  inverted paths on reset and confirm, and any branch added later.
- The rule is one line to follow: a new member starts the floor before its lookup. On the login page
  a new failure branch waits by default.
- The confirm-email page no longer tells a stranger which addresses have a confirmed account.

### Negative / Accepted Trade-offs

- **A typo costs more.** A wrong password or an unknown address now waits 500 ms instead of about
  150 ms, and a resend request waits 3 s. Accepted: these are failure paths or rare actions.
- **More CPU on the branches that skipped the hash.** Each of them now pays one PBKDF2 hash, as the
  unknown-address branch always did.
- **Held requests.** Each waiting request holds a connection and a timer for up to the floor. The
  rate limits bound how many one address can hold; the wait costs no CPU and the request has already
  released its database connection.
- **The floor can be overrun** (decision 3), and then only decision 5 is left.
- **Registration and the e-mail change form still disclose** whether an address is taken (#860).
  Until the owner rules there, this ADR hides nothing that those give away more cheaply.
- **The floor tests prove a lower bound only.** A member whose timer started after its work would
  still pass them. Starting the clock before the lookup and waiting in a `finally` is a structural
  rule of this ADR, checked in review, not by a test.
- **The suite runs with a no-op floor.** An ordinary test would not notice a member that stopped
  waiting; only the floor tests would. They cover every member and every branch in decision 1.

## Alternatives Considered

### A. Make every branch do the same work

Rejected as the main guard. It is what failed three times. CPU can be matched with a dummy hash, but
a database write cannot be faked at the same cost, and a live SMTP send cannot be matched at all.
Kept as the second layer (decision 5).

### B. Count the failed login after the response is sent

Rejected. `Response.OnCompleted` or a queue takes the write off the login answer, but on a keep-alive
connection the next request waits for it, a failed write is invisible, and it fixes one member of the
list, not the others.

### C. Accept the gap and record why

Rejected by the owner. It is cheap, and the rate limits do slow a measurement, but the gaps are real
and include an inverted oracle nobody had noticed.

### D. Add random delay instead of a floor

Rejected. Random noise averages out over enough samples, and the rate limits allow enough samples
over days. A floor removes the signal instead of blurring it.

### E. Fix only the login page

Rejected by the owner. Forgot-password, resend, reset and confirm have the same kind of gap, and a
shared floor costs one call per member. Cancel deletion joins for the uniform rule.

### F. Route the forgot, reset and confirm pages through their handlers

Not now. Three pages duplicate their handler's logic, and the duplication is how the confirm-email
page drifted from its API twin. Moving them onto the handlers would leave one wait per member, but
each page has its own messages and error mapping, so it is a behaviour-preserving refactor of its own.

## Implementation Notes

- New: `Services/ResponseTiming/IResponseTimeFloor.cs`, `ResponseTimeFloor.cs`, `ResponseTimer.cs`,
  `ResponseTimeFloors.cs`; registered in `ApiDependencyInjection.AddAuthApi` as a singleton.
- Members: `Pages/Account/Login.cshtml.cs` (credential check split into one method, decision 2),
  `Pages/Account/ForgotPassword.cshtml.cs`, `Pages/Account/ResetPassword.cshtml.cs`,
  `Pages/Account/ConfirmEmail.cshtml.cs` (also decision 7), and the handlers in
  `Features/Auth/ForgotPassword.cs`, `ResendEmailConfirmation.cs`, `ResetPassword.cs`,
  `ConfirmEmail.cs`, `CancelAccountDeletion.cs` (the resend and cancel pages call their handler and
  inherit the floor).
- Tests: `ResponseTimeFloorTests` (unit, on `FakeTimeProvider`); `ResponseTimeFloorEndpointTests`
  (integration: every branch in decision 1 takes at least its floor; the branches of one member are
  sent together, so a member costs one floor); `AccountLookupHashParityTests` (decision 5, for the
  branches that gained the hash); `ConfirmEmailPageTests` (decision 7). The integration host swaps in
  `NoResponseTimeFloor` with the same exactly-one-registration check it uses for hosted services.
  The floor tests share one host with the real floor (`AuthSystemApiFactory.GetResponseTimeFloorHostAsync`),
  built once and warmed up, that runs no outbox relay so it cannot take another test's outbox row.
- Not members: registration and the e-mail change form (they disclose by design today, #860); the
  password grant on the token endpoint (only on in the Testing environment).

## References

- Ticket #840 and the owner's answers on it (the time floor, applied to every member now).
- #314 and ADR-0056 decision 4: the two earlier per-branch fixes on the login page.
- ADR-0038 decision 4 (resend sends directly) and decision 5 (every path pays the same hash).
- ADR-0046 (the password-proven login branches), ADR-0054 (the per-address page limits),
  ADR-0055 / ADR-0057 (the per-inbox mail budgets).
- #860: registration and the e-mail change form name a taken address.
