# ADR-0063: A Browser That Used a One-Time Link Remembers It, and No Page Looks an Account Up to Say "Done"

**Status:** Accepted
**Date:** 2026-10-06
**Decision-makers:** Solo maintainer (ticket #941)
**Related:** AuthSystem.API (`Pages/Account/UsedLinkCookie.cs`, `ConfirmEmailChange.cshtml.cs`,
`ResetPassword.cshtml.cs`, `wwwroot/submit-once.js`); ADR-0048 (e-mail change and its undo), ADR-0059
(the time floor for answers that hide whether an address has an account); spec 0013; tickets #869, #871,
#886, #941

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

The ticket offered two ways to make the page know:

1. When the form page loads, check whether the link can still be used, and show "already done" or "link
   dead" instead of the button.
2. When the used link comes back on the POST, check whether the account is already in the state the link
   asked for, and answer "done" instead of "link dead".

Both ask the account a question, and the visitor reads the answer. The rule they must keep is that no page
tells a stranger whether an address has an account.

## Decision

### 1. The page that does the work leaves a marker in the browser that sent it

When a confirm or a reset succeeds, the page adds a cookie to its redirect to the done view. The cookie
holds the SHA-256 of the link's token, in base64url, never the token. There is one cookie per flow:

- `.lotrokoniecdev.used-link.email-change` for the confirm of a new address;
- `.lotrokoniecdev.used-link.password-reset` for the password reset.

The cookie is `HttpOnly`, `SameSite=Lax`, `Path=/` and lives 30 minutes. `Secure` follows the request
scheme, the same as the frontend's `.lotrokoniecdev.session-expired`. Lax and not Strict, because the
link's page was first opened from a mail client, and a browser may treat Back to it like that first visit
from another site.

### 2. The link's page shows "done" when this browser used this link

`OnGet` of both pages compares the hash of the link's token with the cookie. A match renders the page's
existing done view in place, the same view the redirect after the POST shows. It prints nothing from the
link and carries no button. Without a match, nothing changes: the form, as before.

The answer depends only on the cookie and the token the visitor sent. The page does not look the address,
the user or the token up, so the answer is the same for an address with an account and one without, and
the time floor of ADR-0059 is not needed here. A stranger can send any cookie they like, but that changes
only the answer to their own request, and the done view tells them nothing.

### 3. The confirm POST reads the marker too; the reset POST deliberately does not

A browser can still show the old form: a second tab on the same link, or a page the browser brought back
from its cache. The POST then sends the used link.

- The confirm form asks for no input, so a second send asks for exactly what the first one did. When the
  marker matches, the POST redirects to the done view without calling the handler.
- The reset form carries a password the user typed again, maybe a different one. The done view says
  "Twoje nowe hasło jest aktywne", which would then name the wrong password. So the reset POST keeps its
  answer: the link is used up.

### 4. A form the browser brings back from its cache asks for a fresh GET

Some browsers restore a page on Back from memory, without asking the server, and then the cookie is never
read. `submit-once.js` already handles that restore for the busy button (#871). A form marked
`data-recheck-when-restored` that was already sent now asks for a fresh GET of its own address instead,
and the server answers as in decision 2. It uses `location.replace`, not `location.reload`: when the
restored page was the answer to a POST, a reload would send that POST again. Only the confirm and reset
forms carry the mark.

## Consequences

### Positive

- Back after a confirmed address or a new password shows the success answer again, with no button that
  calls the link dead.
- No page gained a new question to the account. No new answer differs between an address with an account
  and one without.
- The reload fix of #886 and this fix answer with the same done view, so the user always sees one message.

### Negative / Accepted Trade-offs

- **Only the browser that used the link knows.** The same link opened later on another device still says
  it is dead. That is true there, and that device never showed a success message.
- **The marker reports what this browser did, not the account's state now.** If the address is changed back
  from the old mailbox in between, Back still shows "Od teraz logujesz się nowym adresem". The done view of
  #886 already works this way.
- **A second send of the reset form still says "link dead".** That happens in a second tab or in a cached
  page with JavaScript off. It is accepted, because "done" could name the wrong password as the active one
  (decision 3).
- **Another short-lived cookie on the auth origin.** It fits the privacy policy's "Cookie techniczne
  krótkotrwałe" entry, a one-time state marker, so the policy text does not change.
- **One marker per flow.** Two resets in 30 minutes keep only the second one's marker, so Back to the first
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
is about the browser that just saw the success message, and decision 1 covers that browser with no state on
the server. Rejected for now; revisit if "used on another device" ever needs its own answer.

### D. Fix it only in the browser

`history.replaceState` cannot remove the form from the history, and a script cannot know whether the POST
succeeded. The server is the only one that knows, so the server sets the marker. Rejected as the main fix;
the script in decision 4 only asks the server again.

## Implementation Notes

- `UsedLinkCookie` is a small sealed class with one instance per flow: `Remember` on success,
  `WasUsedHere` on a GET or POST.
- `RevertEmailChange` and `CancelDeletion` redirect to the reset form, so Back from that form leads to a
  used undo or cancel link. They have no done view of their own, and the right words for one are a decision
  for the owner. They stay out of this change.
- Tests: `UsedLinkCookieTests` (unit), `EmailChangePageTests` and `ResetPasswordPageTests` (integration,
  real PostgreSQL), `OneTimeLinkBackTests` (browser: Back from both success pages, and the restore event).

## References

- #941 — the bug: Back from a success page leads to a form that calls the link dead.
- #886 — the reload fix and the done views this reuses.
- #869 — "done" for a second submit whose token still passed.
- #871 — `submit-once.js`, one send per page view.
- ADR-0059 — the time floor for answers that hide whether an address has an account.
