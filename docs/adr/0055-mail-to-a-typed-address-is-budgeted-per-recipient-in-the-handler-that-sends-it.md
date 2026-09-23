# ADR-0055: Mail to a Typed Address Is Budgeted per Recipient, in the Handler That Sends It

**Status:** Accepted (amended 2026-09-23 by ADR-0057: decisions 1, 2 and 4, and the first accepted trade-off. See the notes in the text)
**Date:** 2026-09-23
**Decision-makers:** Solo maintainer (ticket #793)
**Related:** AuthSystem.API (`Features/Auth`, `Services/RateLimiting`, `Program.cs` rate-limit
policies), Frontend (`ApiProblemCopy`), ADR-0038 (one dispatch pipeline for transactional mail),
ADR-0048 (the e-mail change), ADR-0053 (the per-account budget shape), ADR-0054 (which policies see
the visitor's address); tickets #692, #793, #813, #835

## Context

Four flows send mail to an address the caller typed in. Before #793, two of them had nothing but an
IP policy in front of the send:

| Flow | Who can call it | Brake before #793 |
|---|---|---|
| Registration (`RegisterUser`, page and endpoint) | anyone | per IP (`register-limit` on the endpoint, `auth-page-limit` on the page), and one address can register only once |
| Password reset (`ForgotPassword`, page and endpoint) | anyone | `forgot-password-limit` per IP, and 3 mails per 15 min per account (#692) |
| Resend confirmation (`ResendEmailConfirmation`, page and endpoint) | anyone | `resend-confirmation-limit` per IP only |
| E-mail change (`RequestEmailChange`) | a logged-in account | `change-email-limit` per visitor, and the password budget of ADR-0053 |

An attacker who rotates IPs gets a fresh IP budget every time, so they could make the resend flow
send without limit to any address that has an unconfirmed account. Registering the address first
creates such an account. Accounts are cheap, so the same attacker could also use many accounts to
send e-mail change links to one address. Each flood burns the Brevo sending quota and the sender
reputation the whole product depends on.

Code facts that shaped the decision:

- `PerAccountFixedWindowThrottle` is the one class behind every per-account budget: one singleton
  instance per budget, each under its own interface, keyed on the account id (ADR-0053 §1). The id,
  not the typed text, because Identity folds spellings of an address into one account (#692).
- The resend sends directly from the request and skips the outbox on purpose (ADR-0038 decision 4).
  It is the way out when the pipeline itself is stuck.
- The new address of an e-mail change has no account by definition: the handler refuses an address
  that is taken or reserved (`RequestEmailChange.cs`). There is no id to key on.
- `ILookupNormalizer` is registered scoped by `AddIdentityCore`. `EmailConstants.RegexPattern`
  accepts ASCII only, so on every address the flows accept, `NormalizeEmail` is in practice
  upper-casing.
- The change-email page tells the user, on a 200, that a link went to the new address and a warning
  went to the old one (`ChangeEmail.razor`).

## Decision

### 1. Every flow that mails a typed address has a budget per recipient, not only one per IP

- **Resend confirmation**: 3 mails per 15 minutes per unconfirmed account
  (`IEmailConfirmationResendThrottle`, `AccountBudgets.EmailConfirmationResendPermitLimit`).
- **E-mail change**: 3 links per 15 minutes per new address, whichever account asks
  (`IEmailChangeRecipientThrottle`, `AccountBudgets.EmailChangeRecipientPermitLimit`).
- **Password reset** keeps its #692 budget.
- **Registration** needs no throttle of its own: the unique address is its budget. Every spelling
  Identity folds together gets exactly one confirmation mail, and a second registration is refused
  before anything is queued. A test pins that.

All three budgets use the same 15-minute window (`AccountBudgets.Window`), so "come back later"
means the same wait everywhere.

> **Amended by ADR-0057 (#835):** registration now has a budget of its own, 3 new accounts per inbox
> per 15 minutes. The unique address is no budget when an inbox has many spellings.

### 2. The key is the account id where the address has an account, the normalized address where it has none

The resend budget keys on the account the lookup finds, like the password-reset budget. Every
spelling that finds the account shares one budget, and an address with no account spends nothing.

The e-mail change budget is the one exception to "key on the id, never on the address text". It
keys on `UserManager.NormalizeEmail(newEmail)`. That is the same folding Identity uses to decide that
two spellings are one account, so the reason behind #692's rule does not apply: two spellings Identity
would treat as one address also share one budget here. The key may fold more than delivery does. The
mail still goes to the typed spelling, and folding more only makes two spellings share a budget,
which is the safe direction. The handler normalizes, not the throttle, because the normalizer is
scoped and the budget is a singleton.

A per-requester budget would be the wrong key for this flow: an attacker with N accounts would get N
budgets for one inbox. Each account is already limited on its own by the password budget of ADR-0053
(10 per 15 minutes) and by `change-email-limit` per visitor.

> **Amended by ADR-0057 (#835):** the resend and e-mail change budgets now key on the inbox
> (`MailboxKey`), which also folds a `+tag`, Gmail's dots and googlemail.com. The password-reset budget
> keys on the account id for a confirmed account and on the inbox for an unconfirmed one.

### 3. The budget is taken in the handler, only where a mail really goes out, not in the dispatch leg

The ticket asked us to consider the outbox dispatch leg instead, as the one place that sees every
outgoing address. It does not see every address, and it is the wrong moment for a refusal:

- The resend never goes through the outbox (ADR-0038 decision 4), so a dispatch budget would miss
  the flow this ticket is about.
- A dispatch refusal comes after the row is committed and after the caller was told it worked. The
  row would need a new terminal state, and the user could not be told.
- Redelivery and SMTP retries run the dispatch processor again for the same row, so a dispatch
  budget would count retries and drop real mail.
- One `EmailChangeRequested` row sends two mails. One of them is the warning to the old address,
  which the flow cannot afford to lose.

So each handler takes its permit as the last check before the send or the enqueue. A request refused
earlier, for an unknown address, a confirmed account, a wrong password, a taken, reserved or unchanged
address, spends nothing. That also means a stranger cannot empty the budget before the owner asks.

### 4. One class per kind of key, one instance per budget

The ticket asked whether the throttles should become one service keyed by (flow, address). They
already work that way: every budget is its own singleton instance under its own interface (ADR-0053
§1), so the flow is part of the key without being a string anyone can mistype. The resend budget is a
third instance of `PerAccountFixedWindowThrottle`. The e-mail change budget needs a second class,
`PerRecipientFixedWindowThrottle`, only because its key is a string and not a `Guid`.

> **Amended by ADR-0057 (#835):** `PerMailboxFixedWindowThrottle` replaces
> `PerRecipientFixedWindowThrottle` and takes a `MailboxKey`, not a string. The reset budget is
> `PasswordResetRequestThrottle`, which holds one budget per account and one per inbox.

### 5. The resend refusal is invisible; the e-mail change refusal is a 429

- **Resend** answers a refusal with the same success as every other branch. The form is anonymous,
  and a visible refusal would tell a stranger that the address has an unconfirmed account. Nothing is
  lost for the owner: every mail an attacker triggers carries a working link for the owner's own
  account.
- **E-mail change** answers `AuthErrors.EmailChangeRecipientThrottled` (429), and the frontend shows
  its Polish sentence. A silent 200 would make the page say that a link and a warning went out when
  neither did. The budget belongs to the address, so another account can be the one that spent it.
  The refusal does tell the caller that someone asked to mail that address in the last quarter of an
  hour. That is accepted: finding it out means sending mail to that address, which its owner sees,
  and a silent 200 would mislead every real user instead.

## Consequences

### Positive

- One inbox gets at most 3 resent confirmations and 3 e-mail change links per quarter of an hour for
  each spelling Identity folds together, whatever IPs and accounts the requests come from.
- A refused send costs no SMTP call and no outbox row.
- The decision on where budgets live, and on how they are keyed, is in one place for the next
  mail-sending flow.

### Negative / Accepted Trade-offs

- **Sub-addresses and Gmail dots are not folded.** `anna+1@gmail.com`, `a.nna@gmail.com` and
  `anna@googlemail.com` reach one inbox, but every budget here, and registration's one-mail rule,
  treats them as separate addresses. For Gmail inboxes this ADR is therefore a much weaker brake:
  registration is the cheapest channel, at one mail per new spelling behind an IP budget only.
  Folding these belongs in one mailbox key shared by all four flows, not in one flow. That is #835.
  **Resolved by ADR-0057.**
- **Another account can keep a new address's e-mail change budget spent.** It takes three requests
  in every window, and each one mails that address, so the owner of the inbox sees it happen. For as
  long as it goes on, a real change to that address is refused with the 429. A flood brake has to key
  on the recipient, and a key that also named the requester would give every attacker account its own
  budget (decision 2).
- **A permit is spent before the save or the send.** A failed save, a cancelled request or a failed
  SMTP call still costs one, and a fixed window cannot give it back (ADR-0053 §2 makes the same
  choice). A user who hits an SMTP outage three times with the resend waits out the window after SMTP
  recovers.
- **The budgets are in process**, so two containers mean two budgets and a restart empties them. It
  is the same trade-off every limiter in this app makes.
- **The resend refusal is hidden in the response body only.** How long the response takes already
  tells the branches apart (the confirmed branch skips the dummy hash, the send branch waits for
  SMTP). That was true before #793, and this decision does not fix it.
- **A refused e-mail change has already spent the caller's other permits.** The password budget of
  ADR-0053 and `change-email-limit` (3 per hour per visitor) are both taken before this budget, so a
  user who is refused three times within an hour also meets the IP policy for the rest of that hour,
  even though the Polish message says to wait a quarter of an hour. This is by design: the other two
  brakes must count every attempt, and a person almost never meets this case.
- **Each refused request writes one warning line.**

## Alternatives Considered

### A. Take the budget in the dispatch leg

Rejected for the four reasons in decision 3. The strongest one is that the resend never reaches it.

### B. One generic service keyed by (flow, address)

Rejected. Separate instances under separate interfaces already give every flow its own budget, and
the compiler checks which one a handler spends. A flow name in a string key could be mistyped, and
the result would be two flows sharing a budget with nothing to catch it.

### C. Key the resend budget on the typed address

What the ticket first described. Rejected for #692's reason: an account id cannot be spelled two
ways, and an address with no account then spends nothing.

### D. Key the e-mail change budget on the requesting account

Rejected. Accounts are cheap, so the inbox would get one budget per attacker account. The per-account
brakes that exist already (ADR-0053) cover the requester side.

### E. Answer the spent e-mail change budget with a silent 200

The shape the ticket asked for, copied from password reset. Rejected: the page would claim that a
link and a warning were sent, and the budget can be spent by someone else (decision 5).

### F. Fold sub-addresses and Gmail dots into the key now

Deferred to #835. The rules depend on the provider, and folding them in one flow while registration
stays open would move no attacker to a harder path.

## Implementation Notes

> **Amended by ADR-0057 (#835):** the class and test names below are the ones this ADR shipped with.
> The current types and tests are listed in ADR-0057's Implementation Notes.

- `Services/RateLimiting/IEmailConfirmationResendThrottle.cs` (implemented by
  `PerAccountFixedWindowThrottle`), `IEmailChangeRecipientThrottle.cs`,
  `PerRecipientFixedWindowThrottle.cs`, `AccountBudgets.cs`; registered in `ApiDependencyInjection.cs`.
- `Features/Auth/ResendEmailConfirmation.cs` (`EventIds.ResendConfirmThrottled`, 2283) and
  `Features/Auth/RequestEmailChange.cs` (`EventIds.EmailChangeRecipientThrottled`, 2509).
- `ApiErrors/AuthErrors.EmailChangeRecipientThrottled`; its Polish sentence in the Frontend's
  `ApiProblemCopy`.
- The IP policies stay as they are: they still bound how much one client can send.
- Tests: `PerRecipientFixedWindowThrottleTests` (unit); `EmailConfirmationResendBudgetTests`,
  `EmailChangeRecipientBudgetTests` and
  `RegisterEndpointTests.Register_ShouldQueueNoSecondConfirmation_WhenTheAddressIsAlreadyRegistered`
  (integration); `ApiProblemCopyTests` (frontend).

## References

- Ticket #793; #692 (the per-account budget and the reason to key on the id); #813 / ADR-0053 (one
  class, one instance per budget); ADR-0038 decision 4 (the resend skips the outbox); ADR-0048 (nothing
  on the account changes when a change is requested); ADR-0054 §3 (the resend policy stays on the
  connection's address); #835 (sub-addresses and Gmail dots).
