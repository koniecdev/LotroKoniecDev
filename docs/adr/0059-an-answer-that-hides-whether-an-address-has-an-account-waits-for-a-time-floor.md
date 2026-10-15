# ADR-0059: An Answer That Hides Whether an Address Has an Account Waits for a Time Floor

**Status:** Accepted (amended 2026-10-15, #860: registration joins, see "Amendment" below)
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
    > **Amended 2026-10-15 (#860):** the owner ruled that both stop telling. See "Amendment" below.

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
| Registration, page and `POST auth/register` (added by the amendment below, #860) | 1 s |

A new anonymous page or endpoint that looks an account up by a typed address and hides the result
joins this list in the same change.

### 2. Waiting is the default; only a proven secret skips it

Every member hands the work that starts with the lookup to `IResponseTimeFloor.HoldAsync`. The clock
starts inside `HoldAsync` before the work runs, and an exception from the work waits too, then leaves
unchanged. So the rule "clock before the lookup, wait on every exit" holds by construction: a member
cannot start the clock late or forget the wait on one branch. A malformed address is refused before
the lookup and does not wait: that answer depends on the input alone.

- **Handlers** hand over their whole body after validation. Every branch pays the wait, including one
  added later. Reset, confirm and cancel also wait on success: success needs the mailed token, so it
  reveals nothing, and one rule is simpler than two. These actions are rare.
- **The login page** checks the credentials in one method that returns either an account whose
  password it verified, or nothing. Nothing waits. Only a verified account skips the wait
  (`skipWaitFor`), and only
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

Resend is the member most exposed to this. `EmailService` tries a send up to three times, each with a
10 s timeout and a backoff of about 0.5 s and then 1 s between tries. So when the relay has a
passing error or is slow, the unconfirmed branch with a permit can take 4–30 s while every other
branch answers at 3 s, and that shows an unconfirmed account. A floor long enough for the retries
would make every resend wait half a minute, so this is accepted. What limits it: only an unconfirmed
account with a send permit takes that branch, each inbox has 3 permits per 15 minutes (ADR-0055,
ADR-0057), and a failing relay stops mail for every user, so it does not go unnoticed.

### 4. The wait spends no CPU and never ends early

The wait is `Task.Delay` on the injected `TimeProvider`, so a held request costs a timer, not a
thread or a hash. After each delay it checks the elapsed time again and waits for the rest: a timer
can fire a little early (up to about 15 ms on Windows), and the floor must be a lower bound.

The wait does **not** follow the request's cancellation token. It runs after the work, and a
cancelled wait would replace the real outcome with a cancellation: a database exception would be
logged as a harmless client abort, and a committed success would read as cancelled. A caller who
hangs up keeps a timer alive for at most one floor, which costs nothing worth saving.

### 5. Every branch still pays exactly one hash

The equal-CPU rule of ADR-0038 decision 5 stays, as the second layer for an overrun floor, and now
holds on every branch: in forgot-password, resend, reset, confirm and cancel, the dummy hash runs
once on every path right after the lookup, as forgot-password and cancel already did. Resend's
confirmed and throttled branches, and reset's and confirm's real-account branches, gain the hash they
skipped. The login page keeps its per-branch hashes, pinned by `LoginPageTests`. The dummy hash goes
through `UserManager.PasswordHasher`, so the integration host's spy counts it.

### 6. One service, and a no-op floor in the integration suite

`IResponseTimeFloor` (singleton) runs the work through `HoldAsync`; an internal `ResponseTimer`
does the wait. Every member uses it. The integration host replaces it with a floor that never waits, so the hundreds
of tests that post to these pages do not each pay 0.5–3 s. The floor tests put the production
registration back and check, per branch, that the answer takes at least the floor.

### 7. The confirm-email page names success only for a valid token

For an account that is already confirmed, the page now checks the token as well. A valid token (the
owner clicked the mailed link twice) still shows the success page; any other token gets the same
"link not valid" answer as an unknown address. The token stays valid after confirmation, because
confirming does not change the security stamp, so the double click keeps working for the link's 24
hours, unless something else changes the stamp first (a password reset or change, an e-mail change).
After that, or after the 24 hours, the owner of an active account gets "link not valid". The page
cannot tell them their account is active without telling a stranger the same, so the "link not
valid" text now ends with "if your account is already active, just sign in", for every caller.

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
  > **Superseded 2026-10-15 (#860):** neither form names a taken address any more. A two-step probe
  > through registration is left, see amendment decision 14 and #1151.
- **The floor tests prove a lower bound only.** They would not catch a clock that started after the
  work. `HoldAsync` makes that impossible for a member that uses it (decision 2); a member that does
  not call it at all fails its floor test.
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

- New: `Services/ResponseTiming/IResponseTimeFloor.cs` (`HoldAsync`), `ResponseTimeFloor.cs`,
  `ResponseTimer.cs`, `ResponseTimeFloors.cs`; registered in `ApiDependencyInjection.AddAuthApi` as a
  singleton.
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
  built once and warmed up with one account lookup, that runs no outbox relay so it cannot take
  another test's outbox row.
- Not members: the password grant on the token endpoint (only on in the Testing environment), and the
  e-mail change request, which no longer looks the new address up (amendment decision 12). Registration
  joined in #860.

## Amendment 2026-10-15 (#860): registration and the e-mail change form stop telling

The owner ruled on #860: stop telling strangers whether an e-mail address has an account, wherever we
can. Registration always answers "check your inbox"; the owner of a taken address gets a notice; the
e-mail change form always says a link went out, and the owner of a taken new address gets a notice
instead of the link; a reserved address (#684) mails nobody; a taken username still says "taken",
because the username is a public handle.

### 8. Registration answers the same for a free, a taken and a reserved address

`POST auth/register` answers `202 Accepted` with no body, and the register page shows one "check your
inbox" panel. A new account's id was the one thing in the old `201` body a taken address could not
copy, so it is gone. Nobody read it: the frontend never calls this endpoint, and the tests now take the
id from the confirmation mail.

- A free address creates the account and queues the confirmation, as before.
- A taken address creates nothing and queues `RegistrationAttemptedOnExistingAccount` with the owner's
  id and the typed address. Its processor mails the owner a notice ("someone tried to register with
  this address; if it was you, sign in or reset your password"). If the account moved to another
  address while the message waited, the notice is dropped: it would tell the new address about an
  attempt on the old one.
- A reserved address creates nothing and queues nothing: it has no account to tell.
- A registration that loses the race for its address, at the unique index or at Identity's own check
  inside `CreateAsync`, answers like a taken address and spends a permit like it, with no notice: the
  winner's confirmation already goes to that inbox. A lost race for the username still says "taken".

### 9. What runs before the address lookup, and in which order

Every check whose answer does not depend on the address runs first, so its answer is the same for a
free and a taken address:

1. The command validator.
2. Every Identity password validator, the breach check included (ADR-0066). The handler runs them
   itself, because `UserManager.ValidatePasswordAsync` is protected. Before #860 the breach check ran
   inside `CreateAsync`, so only a free address paid for it and only a free address could answer
   "this password leaked".
3. One PBKDF2 hash of the typed password. A free address stores it with the no-password
   `CreateAsync(user)` overload, which runs no password validator, so the breach service is called
   once. A taken or reserved address throws it away. That is decision 5 for registration: every branch
   pays exactly one hash, before the branches split.
4. Inside the floor and the transaction: the username, then the address. The username comes first,
   because checked after the address it would answer "taken" only where the address was free.

Steps 2 and 3 run outside the floor and outside the transaction: they depend on the password only,
and the breach call (up to 3 s) must not hold a database connection.

### 10. Registration waits for 1 s

The free branch makes about 17 database round trips: three lookups by name and three by address
(ours, `CreateAsync`'s user validator, and the one inside `AddToRoleAsync`), the reservation check,
two role lookups and one user-role lookup, three saves each followed by its own savepoint release, and
the commit. A taken address makes about 5, a reserved one about 4. A TCP connect from the staging box
to Neon's `eu-central-1` proxy took 6 to 10 ms over 8 samples (2026-10-15), so the free branch spends
about 170 ms on the network, plus the CPU of EF and Identity. 500 ms would be under two and a half
times that on a slow tail, so registration gets its own constant, `ResponseTimeFloors.Registration`.
The hash and the breach call are outside the clock (decision 9). A rare action can wait a second.

### 11. Every branch spends the same registration permit

The per-inbox budget (ADR-0057) is taken on the free, the taken and the reserved branch, and on a lost
race for the address, as the last check before the enqueue, keyed on the typed address. A spent budget answers 429 on all three. If the
taken branch spent nothing, as ADR-0055 §3 and ADR-0057 §4 said, the fourth try would answer 429 only
for a free address. The owner of a taken inbox therefore gets at most three notices a quarter hour.

### 12. The e-mail change request never looks the new address up

The handler checks the account, the password and that the new address differs from the current one,
takes the recipient permit (ADR-0055) and queues `EmailChangeRequested`, whatever the new address is.
Nothing in the request depends on the new address, so it needs no floor. The dispatch processor
decides when it sends:

- The warning to the current address goes out first, on every branch. The requester reads that inbox,
  so a warning that went out only for a free address, or later for one, would tell them the branch.
  It is also the mail this flow can least afford to lose.
- A new address with another account gets a notice to its owner instead of the link.
- A reserved address gets nothing.
- Otherwise the link goes out, as before.

The confirm leg still refuses a taken or reserved address when the link is used, so moving the
decision from the request to the dispatch loses no safety.

Trade-off: a stranger's requests to a reserved address spend that inbox's recipient budget, like for
any other address, so the owner's own move back to it through the form can wait a quarter of an hour.
Before #860 the reservation check came first and spent nothing, but a permit spent on every address
except a reserved one would show which addresses are reserved. The undo link in the change notice, the
path the reservation exists for, takes no budget and is not affected.

### 13. What a stranger can still observe

The answers, their timing above the floor, the budget and the requester's own inbox are the same on
every branch. Only the owner of the address learns which branch ran, from the mail that reaches them.

### 14. Known gap: a two-step probe through registration (#1151)

A free address gets a real account, with the username and password the caller typed. The caller can
then ask about that account: sign in with the password (an unconfirmed account answers "not activated
yet", ADR-0046), or register again with the same username (it is now taken). Each probe costs two
requests. Sent one after the other, they create an account on a free address and mail its owner. Sent
at the same moment, with the second on an address the caller owns, the free address can lose the race
for the username and keep no trace at all. Closing it needs a UX change
(e-mail-first registration, or a sign-in page that stops naming an unconfirmed account), so it is the
owner's call and is tracked in #1151.

## References

- Ticket #840 and the owner's answers on it (the time floor, applied to every member now).
- #314 and ADR-0056 decision 4: the two earlier per-branch fixes on the login page.
- ADR-0038 decision 4 (resend sends directly) and decision 5 (every path pays the same hash).
- ADR-0046 (the password-proven login branches), ADR-0054 (the per-address page limits),
  ADR-0055 / ADR-0057 (the per-inbox mail budgets).
- #860: registration and the e-mail change form name a taken address (the amendment above).
- #1151: the two-step probe left after #860.
