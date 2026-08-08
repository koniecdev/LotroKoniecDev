# ADR-0046: The unconfirmed-e-mail login branch names its reason, behind a verified password

**Status:** Accepted
**Date:** 2026-08-08
**Decision-makers:** Solo maintainer
**Related:** #560 (the defect), #554 (the QA ticket whose expectation it contradicted), ADR-0022 (e-mail is the login identifier), ADR-0031 (the deletion grace period whose branch set the precedent), `Login.cshtml.cs`, `ResendConfirmation.cshtml.cs`, `SensitiveDataRedactor`

## Context

`/Account/Login` answered every credential failure with one shared sentence:

> Nieprawidłowy e-mail lub hasło. Jeśli konto zostało dopiero co utworzone, dokończ rejestrację,
> potwierdzając swój adres e-mail — kliknij link aktywacyjny, który do Ciebie wysłaliśmy.

A QA tester logged in with a correct password on an unconfirmed account, was told the credentials
were invalid, and filed it as a bug. The report was closed as by design on the anti-enumeration
rationale, then reopened: **that rationale does not reach this branch.**

The handler's branches, in execution order, split cleanly by what a caller must already know:

| # | Branch | Reachable without a valid password? |
|---|---|---|
| 1 | user not found | yes (+ dummy hash for timing parity) |
| 2 | deletion scheduled | **no** — verifies the password first, then names the deletion date |
| 3 | locked out | yes (+ dummy hash) |
| 4 | wrong password | yes |
| 5 | **e-mail not confirmed** | **no** — runs *after* branch 4 |

Branches 1, 3 and 4 must stay identical: they answer someone who has proven nothing, so any
difference between them tells an attacker whether an address is registered. That is real account
enumeration and the generic message is the right answer there.

Branch 5 is not in that set. It is only reachable by a caller who has already presented the correct
password, and branch 2 — one screen up, in the same handler — already returns a *specific* message
in exactly that position, naming the scheduled deletion date. So the two branches have identical
exposure, and the codebase had already accepted revealing a reason at that point. Staying generic at
branch 5 was an inconsistency, not a security boundary.

The cost of being vague was not cosmetic. A user who never received the activation e-mail was told
their password was wrong, so the corrective action they were nudged towards — the password reset —
is the one action that cannot fix their account. The trailing activation hint was the compromise
that was supposed to cover this, and it demonstrably did not: the tester read the first sentence,
which asserts something false, and stopped.

## Decision

### 1. Branch 5 gets its own message; branches 1, 3 and 4 keep the shared one

The unconfirmed-e-mail branch says what is actually wrong, and offers the action that fixes it — a
link to `/Account/ResendConfirmation` carrying the address so the form arrives prefilled.

The shared message loses its trailing activation hint and shrinks to `Nieprawidłowy e-mail lub
hasło.` The hint existed solely as the compromise for the case that now has its own message; on the
three branches that keep the generic text it was noise appended to every mistyped password.

**The invariant that replaces "all branches are identical":** the specific text and the resend link
are reachable **only** behind a verified password. A wrong password against an unconfirmed account
returns the generic message, exactly like a wrong password against any other account — so nothing
about an address can be learned without already holding its password. This is what the tests pin;
it is the property that matters, and the old "every branch is byte-identical" assertion was a proxy
for it that also forbade branch 2.

### 2. The residual leak, stated plainly

A caller holding a correct password for an **unconfirmed** account now learns that the password is
correct, where before they could not distinguish it from a wrong one.

Accepted, because the marginal information is small:

- For a **confirmed** account the same pair already yields a session, which is a louder confirmation
  than any message.
- Reaching the branch requires the password, so it adds nothing to address enumeration — the thing
  the generic message exists to prevent.
- Failed attempts still count towards lockout (`AccessFailedAsync` on branch 4), so it is not a
  cheap oracle to grind.

Rejected alternative: **keep one generic message but reword it** so it no longer asserts the
credentials are wrong (`Nie udało się zalogować. Sprawdź dane logowania lub dokończ rejestrację…`).
It leaks nothing and fixes the literal complaint, but it leaves the genuine new user guessing which
of two unrelated things went wrong, and it keeps branch 2 as an unexplained exception to a rule the
codebase claims to follow. Fixing the inconsistency is worth more than the sliver of ambiguity.

Also rejected: **auto-resending** the activation link from the login branch. It turns the login form
into a mail-sending vector keyed by a password guess, and the user already has a rate-limited page
that does it deliberately.

### 3. The prefilled address forced the request-log redactor to grow

The link is `/Account/ResendConfirmation?email=<escaped>`, which is the first place in the app that
puts an e-mail in a **query string**. `SensitiveDataRedactor.RedactQueryString` masks e-mails in
request logs, but it matched the raw query text on a literal `@` — and `Uri.EscapeDataString` writes
`%40`. Shipping the link without touching the redactor would have persisted whole addresses in the
logs of every environment.

The redactor now recognises both spellings of the separator, so `?email=alice%40example.com` logs as
`?email=a***%40example.com`. Decoding the query before matching was rejected: the function returns
the query verbatim apart from the redactions, so decoding would have to be undone on the way out,
and a decode step in a logging hot path is a new way to throw on malformed input.

## Consequences

### Good

- The one message a blocked new user reads now names their actual problem and links to the fix,
  one click away, with the address already filled in.
- The handler is internally consistent: reason-specific behind a verified password (branches 2, 5),
  uniformly opaque in front of one (branches 1, 3, 4).
- The generic message is a single short sentence again, instead of one that appends registration
  advice to every typo.
- Request logs mask percent-encoded addresses, which was a latent gap the moment anything linked an
  e-mail through a query string — and something eventually would have.

### Neutral

- `LoginPage_ShouldReturnIdenticalMessage_ForEveryCredentialFailure` was a deliberately pinned
  assertion and it changed deliberately. It still runs, over the three branches the rule applies to,
  and a new sibling test pins the invariant that replaces it for branch 5.
- QA #554's expectation ("the message tells the user to confirm rather than claiming wrong
  credentials") is now what the code does; the "disputed expectation" note on that ticket can go.

### The limit of this decision

It says nothing about the **lockout** branch, which stays generic on purpose — it runs before the
password check, so naming it would leak account existence to an unauthenticated caller. If lockout
is ever moved behind the password check, that is a separate decision and needs its own reasoning,
not an appeal to this one.
