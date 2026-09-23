# ADR-0057: Mail Budgets Count per Inbox, but a Confirmed Account Keeps Its Own Reset Budget

**Status:** Accepted
**Date:** 2026-09-23
**Decision-makers:** Solo maintainer (ticket #835)
**Related:** AuthSystem.API (`Services/RateLimiting`, `Features/Auth`, `Pages/Account`), ADR-0053 (the
per-account budget shape), ADR-0055 (the per-recipient budgets, amended here); tickets #692, #793, #835

## Context

Many mail providers deliver several spellings of an address to one inbox:

- a `+tag` after the name: `anna+1@gmail.com` reaches `anna@gmail.com` (Gmail, Outlook, Fastmail and
  others);
- at Gmail, dots in the name: `a.nna@gmail.com` reaches `anna@gmail.com`;
- `googlemail.com` is the same service as `gmail.com`.

Before this decision, every mail budget folded an address the way Identity does, which is letter case
and Unicode form and nothing more:

| Flow | Budget before #835 | Key |
|---|---|---|
| Registration | none of its own; one address registers once | the address after `NormalizeEmail` |
| Password reset | 3 per 15 min | the account id |
| Resend confirmation | 3 per 15 min | the account id |
| E-mail change | 3 per 15 min | the new address after `NormalizeEmail` |

So each new spelling got a fresh budget in every flow. Registration was the cheapest way in: each new
spelling registers a new account, each registration mails the inbox once, and the only other brake is
an IP policy, which rotating IPs get around. Every one of those accounts then had its own reset and
resend budget. ADR-0055 recorded this gap as an accepted trade-off and pointed here.

The first plan keyed every budget on the inbox, and it had a hole. An independent check found it before
any flow code was written:

1. A stranger registers `anna+x@gmail.com` once. The account stays unconfirmed, because its
   confirmation link goes to Anna's inbox.
2. The stranger asks for a password reset on that account 3 times every 15 minutes, from one IP.
   Neither the request (`ForgotPassword`) nor the send (`PasswordResetRequestProcessor`) checks whether
   the account is confirmed, so every request spends a permit.
3. With an inbox key, those permits come out of Anna's budget. Her own reset request is then refused,
   and the refusal looks like success on purpose (ADR-0038 decision 5). None of the mails she got can
   reset her own password.

The account-id key did not have this hole. The only way to spend Anna's budget was to ask for resets
of her own account, and every one of those mails gave her a working link (ADR-0055 decision 3).

Code facts that shaped the decision:

- Only the inbox owner can confirm an account at that inbox, because the confirmation link goes there.
  So every confirmed account at one inbox belongs to its owner, and every account a stranger creates at
  it stays unconfirmed.
- The resend refuses a confirmed account before it sends anything (`ResendEmailConfirmation.cs`), so
  only unconfirmed accounts ever spend its budget.
- Registration runs inside an EF execution strategy with `EnableRetryOnFailure`, so a transient error on
  the commit replays the whole transaction. A transient error while saving is caught by the handler as
  a taken address and does not replay; that is an older defect, #845.
- A user found by `FindByEmailAsync` always has a `NormalizedEmail`, and it equals the normalized typed
  address. `AddIdentityCore` registers the default normalizer, `Normalize()` then upper case.

## Decision

### 1. One mailbox key for every mail budget

`MailboxKey.FromNormalizedEmail` turns Identity's normalized address into the key of the inbox it
reaches:

- It drops a `+tag` from the name, for every domain. A `+` in the first place is kept, because no
  mailbox name comes before it.
- For `gmail.com` and `googlemail.com`, it also drops every dot from the name and treats both domains as
  `gmail.com`.

The key is built from Identity's normalized form, `ApplicationUser.NormalizedEmail` or
`UserManager.NormalizeEmail`. So every pair of spellings that Identity treats as one account also shares
one key, whatever the normalizer does. The key only counts sends. The mail still goes to the address as
the user typed it, and the address stays the account's own: Identity still treats `anna+1@gmail.com` and
`anna@gmail.com` as two accounts.

A key that folds too much only makes two real inboxes share one budget, which is safe. A key that folds
too little is the hole this ticket closes.

### 2. Which budget each flow takes

| Flow | Budget | Key |
|---|---|---|
| Registration | 3 new accounts per 15 min (new) | the inbox of the new account |
| Password reset, confirmed account | 3 per 15 min (#692) | the account id |
| Password reset, unconfirmed account | 3 per 15 min, shared by all unconfirmed accounts at one inbox | the inbox |
| Resend confirmation | 3 per 15 min | the inbox |
| E-mail change | 3 per 15 min | the inbox of the new address |

Each flow keeps its own budget. The flows do not share one.

### 3. A confirmed account keeps a reset budget of its own

A confirmed account belongs to the inbox owner, so a budget per account keeps ADR-0055's property:
every permit a stranger spends mails the owner a working link. Unconfirmed accounts share one budget per
inbox, so each extra spelling a stranger registers brings no fresh budget.

Keeping both budgets side by side does not close the hole. The inbox budget would still refuse Anna's
request, whatever her account budget says.

### 4. Registration takes its permit last, once, and refuses out loud

- The permit is the last check before the confirmation mail is queued: after the taken address, the
  reserved address, the taken user name and Identity's own checks. A registration refused by any of
  those spends nothing.
- A refusal returns without a commit, so the new account is rolled back with it.
- A replay of the transaction after a transient database error does not take a second permit for the
  same registration.
- The refusal is a 429 (`AuthErrors.RegistrationMailboxThrottled`), and the register page shows a
  Polish sentence. A silent success would tell the user to check an inbox that gets nothing. This is
  the same reasoning as ADR-0055 decision 5.

### 5. The key is a type, not a string

Every mail budget takes a `MailboxKey`, so every budget goes through the one fold that type owns. The key
also applies Identity's default fold itself (`Normalize()`, then upper case), so an address passed as typed
by mistake still lands in the right budget. Its text form is masked, like every logged address. The
ForgotPassword page and endpoint both call the same `IPasswordResetRequestThrottle.TryAcquire(user)`,
so the confirmed and unconfirmed rule lives in one class, `PasswordResetRequestThrottle`. The
per-inbox budgets are instances of `PerMailboxFixedWindowThrottle`, which replaces
`PerRecipientFixedWindowThrottle`. `PerAccountFixedWindowThrottle` stays for the password-confirmation
brake (ADR-0053) and for the confirmed half of the reset budget.

## Consequences

### Positive

- In any quarter of an hour, one inbox gets at most 3 registration mails, 3 resent confirmations, 3
  resets for all unconfirmed accounts together, 3 e-mail change links, and 3 resets for each account
  its owner confirmed herself. This holds whatever IPs and accounts the requests use, for every spelling
  the key folds.
- A stranger cannot block a confirmed user's password recovery with an account at a spelling of her
  inbox.
- The next mail-sending flow has one key to use and one place that says which budget fits.

### Negative / Accepted Trade-offs

- **While the owner's own account is still unconfirmed, a sustained attack can hold back her resend
  and her reset.** The stranger's unconfirmed accounts share the inbox budget with hers. Her first
  confirmation mail was already queued when she registered. A budget per account would avoid this, but
  it would give every account a stranger registers a fresh budget again.
- **A stranger can hold back a registration at a spelling of the owner's inbox** by registering three
  accounts there in every window. Each of those mails the inbox. This is weaker than what was already
  possible: registering her exact address blocks it for good, because no job removes an unconfirmed
  registration.
- **The registration refusal tells an anonymous caller that three accounts were registered at this
  inbox in the last quarter of an hour.** The caller only gets it for a registration that would
  otherwise succeed, and each of those three registrations mailed the inbox.
- **The `+tag` fold applies to every domain.** At a provider that does not treat `+` as a tag,
  `john+doe@example.com` can be another person. The two then share one budget, and a stranger's mail to
  the `+` spelling bounces, so the owner does not see her budget being spent. Folding more is the safe
  direction for flooding, and a list of providers would never be complete.
- **A stranger can keep an inbox's e-mail change budget spent through any spelling of it**, not only
  through the exact address that ADR-0055 accepted. The owner of a new address then gets the 429 for as
  long as it goes on. At a provider that delivers `+tag` mail, she sees every link that spent it.
- **Other providers' rules are not folded:** a `-` tag (Yahoo), a subdomain tag (Fastmail), dots
  outside Gmail, and a catch-all domain that delivers every name to one inbox. No key can fold a
  catch-all. A new rule goes into `MailboxKey` and nowhere else.
- **A registration whose save or commit fails for good has still spent its permit.** The permit comes
  before the save, and a fixed window cannot give it back. ADR-0053 §2 and ADR-0055 make the same
  choice. Today a transient error while saving is one of those failures, because the handler catches
  it as a taken address (#845).
- **A refused registration still used Identity's work.** The account row and the password hash are
  created and then rolled back, which costs the same as a successful registration.
- **The budgets are in process**, as before: two containers mean two budgets, and a restart empties
  them.

## Alternatives Considered

### A. One budget per inbox for every password reset

The first plan, and the answer given at the ticket's question gate. Rejected for the attack in the
Context: a stranger's unconfirmed account at a spelling of the inbox would use up a confirmed owner's
reset budget, with mails that cannot reset her password.

### B. Keep the per-account budgets and add per-inbox budgets next to them

The ticket's own wording. Rejected: the inbox budget still refuses the owner, so the hole in A stays.
For the resend, the added account budget would never refuse anything the inbox budget allows.

### C. One budget per inbox shared by all four flows

Rejected. A stranger's registrations would use up the owner's password-reset budget, and a real user
who registers, asks for two resends and then resets a password could meet the limit.

### D. Fold the address itself, so that Identity treats the spellings as one account

Rejected. It would change who owns which account, and existing accounts could already hold two
spellings of one inbox. The key is only for counting mail. `EmailChangeRevertReservationTests` pins that
a `+tag` spelling is still a different address.

### E. Fold `+tag` only for providers known to support it

Rejected. A list of providers is never complete, and each missing one would be a fresh budget per
spelling again. Folding too much only makes two inboxes share one budget.

### F. Limit how many unconfirmed accounts one inbox may have

Rejected. It needs the key stored in a column and a migration. A stranger's unconfirmed accounts would
then block the owner's registration for good, because nothing removes them.

## Implementation Notes

- `Services/RateLimiting/MailboxKey.cs`, `PerMailboxFixedWindowThrottle.cs`,
  `PasswordResetRequestThrottle.cs`, `IRegistrationMailboxThrottle.cs`, `AccountBudgets.cs`
  (`RegistrationPermitLimit`); registered in `ApiDependencyInjection.cs`.
- `Features/Auth/RegisterUser.cs` (`EventIds.RegisterMailboxThrottled`, 2273),
  `Features/Auth/ForgotPassword.cs`, `Pages/Account/ForgotPassword.cshtml.cs`,
  `Features/Auth/ResendEmailConfirmation.cs`, `Features/Auth/RequestEmailChange.cs`.
- `ApiErrors/AuthErrors.RegistrationMailboxThrottled`; its Polish sentence in
  `Pages/Account/Register.cshtml.cs`.
- Tests: `MailboxKeyTests`, `PerMailboxFixedWindowThrottleTests`, `PasswordResetRequestThrottleTests`
  (unit); `MailboxBudgetTests` (integration), including the attack in the Context through both the page
  and the endpoint, and a replayed registration commit.

## References

- Ticket #835 and its comments (the question gate and the plan change); ADR-0055 (amended: decisions 1,
  2 and 4, and its first accepted trade-off); ADR-0053 §1 (one instance per budget); #692 (why a budget
  keys on the account id and not on the typed text).
