# ADR-0063: A Browser That Used a One-Time Link Gets Its First Answer Again, and No Page Looks an Account Up for It

**Status:** Accepted
**Date:** 2026-10-06
**Decision-makers:** Solo maintainer (ticket #941)
**Related:** AuthSystem.API (`Pages/Account/UsedLinkCookie.cs`, `Pages/Account/UsedLinkNextStepCookie.cs`,
`ConfirmEmailChange.cshtml.cs`, `ResetPassword.cshtml.cs`, `RevertEmailChange.cshtml.cs`,
`CancelDeletion.cshtml.cs`, `wwwroot/submit-once.js`); ADR-0031 (two-phase account deletion), ADR-0048
(e-mail change and its undo), ADR-0059 (the time floor for answers that hide whether an address has an
account); spec 0013; tickets #869, #871, #886, #941

## Context

Someone confirms a new e-mail address, or sets a new password from a reset link, and sees the success
message. Then they press the browser's Back button. They land on the form the link opened, with a live
button. If they think the change did not stick and press the button again, the page says the link is
invalid or expired. The change had worked, but the last thing they see says it failed.

The page before the success message in the browser history is the form the one-time link opened. #886 made
the success answer a redirect to a done view, so a reload no longer sends the used link again. Back is a
different path: it loads the link's own page, and the auth server sends every page with
`Cache-Control: no-store`, so the browser asks the server again. That page only showed the form. It never
knew that this link was already used.

The undo link of an e-mail change and the cancel link of a scheduled deletion have the same gap one step
later. Both end by sending the visitor to the password form with a fresh reset token, because the undo
clears the password and the cancel asks for a new one. Back from that password form loads the used undo or
cancel link, and its button calls the link dead.

The ticket offered two ways to make a page know:

1. When the form page loads, check whether the link can still be used, and show "already done" or "link
   dead" instead of the button.
2. When the used link comes back on the POST, check whether the account is already in the state the link
   asked for, and answer "done" instead of "link dead".

Both ask the account a question, and the visitor reads the answer. The rule they must keep is that no page
tells a stranger whether an address has an account.

## Decision

The browser that used a link gets the link's first answer again. The page that did the work leaves a cookie
in that browser, and the link's page reads it. No page asks the account anything to answer.

### 1. The confirm and reset pages leave a marker

When a confirm or a reset succeeds, the page adds a cookie to its redirect to the done view. The cookie
holds the SHA-256 of the link's token, in base64url, never the token:

- `.lotrokoniecdev.used-link.email-change` for the confirm of a new address;
- `.lotrokoniecdev.used-link.password-reset` for the password reset.

### 2. The undo and cancel pages leave an encrypted next step

Their first answer is a redirect to the password form, and that form needs the fresh reset token. So the
cookie holds the hash of the used link's token, the address and the fresh reset token, encrypted with Data
Protection under its own purpose, one sub-purpose per flow:

- `.lotrokoniecdev.used-link.email-change-revert` for the undo of an e-mail change;
- `.lotrokoniecdev.used-link.deletion-cancel` for the cancel of a scheduled deletion.

The reset token already went to this browser in the redirect and sits in its history, so the cookie gives
it nothing new. The encryption stops anybody from reading it out of the cookie, and stops a forged cookie
from sending the browser anywhere: a value that does not decrypt marks nothing.

### 3. All four cookies have the same shape

`HttpOnly`, `SameSite=Lax`, `Path=/`, 30 minutes. `Secure` follows the request scheme, the same as the
frontend's `.lotrokoniecdev.session-expired`. Lax and not Strict, because the link's page was first opened
from a mail client, and a browser may treat Back to it like that first visit from another site.

### 4. The link's page answers from the cookie

- The confirm and reset `OnGet` render the page's existing done view in place when the cookie names the
  link. It prints nothing from the link and carries no button.
- The undo and cancel `OnGet` redirect to the same password form as the first time. If the reset is done
  by then, the reset's own marker shows that form as done (decision 4, first point).

Without a match, nothing changes: the form, as before. On the confirm and undo pages the link's values
must first pass the shape check that already runs there.

The answer depends only on the cookie and the link the visitor sent. No page looks the address, the user or
the token up, so the answer is the same for an address with an account and one without, and the time
floor of ADR-0059 is not needed here. A stranger can send any cookie they like, but that changes only the
answer to their own request: the done view tells them nothing, and only a cookie this server encrypted
leads to a password form.

### 5. The POST reads the cookie too, except on the reset page

A browser can still show the old form: a second tab on the same link, or a page the browser brought back
from its cache. The POST then sends the used link.

- The confirm, undo and cancel forms ask for no input, so a second send asks for exactly what the first
  one did. When the cookie matches, the POST gives the first answer without calling the handler.
- The reset form carries a password the user typed again, maybe a different one. The done view says
  "Twoje nowe hasło jest aktywne", which would then name the wrong password. So the reset POST keeps its
  answer: the link is used up.

### 6. A form the browser brings back from its cache asks for a fresh GET

Some browsers restore a page on Back from memory, without asking the server, and then the cookie is never
read. `submit-once.js` already handles that restore for the busy button (#871). A form marked
`data-recheck-when-restored` that was already sent now asks for a fresh GET of its own address instead,
and the server answers as in decision 4. It uses `location.replace`, not `location.reload`: when the
restored page was the answer to a POST, a reload would send that POST again. It drops the `#fragment`,
because a replace to the same address with a fragment only scrolls. The four one-time-link forms carry
the mark; the login form does not.

## Consequences

### Positive

- Back after a confirmed address or a new password shows the success answer again, with no button that
  calls the link dead. Back from the password form after an undo or a cancel opens that password form
  again.
- No page gained a new question to the account. No new answer differs between an address with an account
  and one without.
- The reload fix of #886 and this fix answer with the same done view, so the user always sees one message.
- No new words on any page: every answer is one the page already gives.

### Negative / Accepted Trade-offs

- **Only the browser that used the link knows.** The same link opened later on another device still says
  it is dead. That is true there, and that device never showed a success message.
- **The cookie reports what this browser did, not the account's state now.** If the address is changed
  back from the old mailbox in between, Back still shows "Od teraz logujesz się nowym adresem". The done
  view of #886 already works this way.
- **A second send of the reset form still says "link dead".** That happens in a second tab or in a cached
  page with JavaScript off. It is accepted, because "done" could name the wrong password as the active one
  (decision 5).
- **A live reset token rides in a cookie for 30 minutes.** It is encrypted and `HttpOnly`, and the same
  browser already holds it in its history. Once the reset is done, the token is dead.
- **The undo and cancel cookies depend on the Data Protection keyring.** After the keyring is lost, the
  cookie decrypts to nothing and the page shows its form, as before this change.
- **More short-lived cookies on the auth origin.** They fit the privacy policy's "Cookie techniczne
  krótkotrwałe" entry, a state marker, so the policy text does not change. That entry names one cookie as
  an example and says it lives tens of seconds; these live 30 minutes. Naming them there is legal text and
  the owner's call.
- **One cookie per flow.** Two resets in 30 minutes keep only the second one's marker, so Back to the first
  form shows the form. That needs a second reset link inside 30 minutes, and the first link is dead anyway.

## Alternatives Considered

### A. Check the link when the form page loads (the ticket's first direction)

The token tells "still good" from "not good", but not "used" from "expired" or "never valid". A reset
token dies when the security stamp changes, and an e-mail change, a password change or a scheduled
deletion change it too. "Done" would then be a guess. Telling them apart for real needs a record of
which token was used, so a new column and a migration. For the confirm link, "the account is already on
this address" is a question about the account, and the answer goes to whoever holds the user id and an
address. Rejected.

### B. Answer "done" on the POST when the account is already in the asked state (the second direction)

For the confirm link, this is the account question of A. For the reset, "the asked state" is the new
password, so the page would compare the typed password with the stored hash. That is a password check
with no lockout, open to anybody who knows an address. #869 does this only for a submit whose token still
passed, the double-click race. Rejected.

### C. Mark the token as used in the database

This tells "used" from "dead" on any device. But it needs a migration, a write on every use, and an answer
that reads the account, so it needs the time floor and a test that the timing hides the answer. The ticket
is about the browser that just saw the success message, and the cookies cover that browser with no state on
the server. Rejected for now; revisit if "used on another device" ever needs its own answer.

### D. Fix it only in the browser

`history.replaceState` cannot remove the form from the history, and a script cannot know whether the POST
succeeded. The server is the only one that knows, so the server sets the cookie. Rejected as the main fix;
the script in decision 6 only asks the server again.

### E. Give the undo and cancel pages a done view of their own

That needs new words for a state no page shows today, and they would still have to send the visitor on to
the password form. Redirecting to that form is the answer the page already gives. Rejected.

### F. Mint a new reset token when a used undo or cancel link comes back

The used link no longer verifies, so nothing would prove who is asking. A stale link from a mailbox would
then hand out a working reset token. Rejected.

## Implementation Notes

- `UsedLinkCookie` is a small sealed class with one instance per flow: `Remember` on success,
  `WasUsedHere` on a GET or POST.
- `UsedLinkNextStepCookie` is a singleton service built on `IDataProtectionProvider`, with
  `Remember(flow, usedToken, nextStep)` and `NextStepFor(flow, usedToken)`. A value that does not decrypt
  or parse marks nothing.
- Tests: `UsedLinkCookieTests` and `UsedLinkNextStepCookieTests` (unit); `EmailChangePageTests`,
  `ResetPasswordPageTests`, `DeletionGraceWindowTests`, `EmailChangeSaveFailureTests` and
  `SubmitOnceFormTests` (integration, real PostgreSQL); `OneTimeLinkBackTests` (browser: Back from every
  success page, and the restore event). The integration tests also pin that a refused POST leaves no
  cookie: a cookie after a refusal would make Back claim a change that never happened.

## References

- #941 — the bug: Back from a success page leads to a form that calls the link dead.
- #886 — the reload fix and the done views this reuses.
- #869 — "done" for a second submit whose token still passed.
- #871 — `submit-once.js`, one send per page view.
- ADR-0059 — the time floor for answers that hide whether an address has an account.
