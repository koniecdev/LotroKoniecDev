# ADR-0052: The GDPR export asks for the password, and the account resource splits from it

**Status:** Accepted
**Date:** 2026-09-20
**Decision-makers:** Solo maintainer (ticket #690 / SEC-12)
**Amends:** [ADR-0032](0032-gdpr-export-tms-leg-via-frontend-composition.md) (the export's entry point and its failure shape), [ADR-0040](0040-authorization-aware-hateoas-links-and-an-anonymous-tms-root.md) (which rel is the auth-side login marker)
**Related:** #689 (SEC-11, TOTP + step-up), #686 / ADR-0049 (five-minute access tokens), ADR-0041 (no gateway)

## Context

`GET auth/account/data-export` asked for a bearer token and nothing else. Every other sensitive
account action asks for the current password: deletion, e-mail change, password change. The export
produces one tidy file with the whole account in it, which is what turns a takeover into a data set.

Two facts made this more than "add a password to the endpoint":

1. **The export was also the account resource.** Every account page (`/account`, change password,
   change e-mail, delete) loaded it to show the account and to read its links. A password on that
   endpoint would have locked the pages themselves.
2. **Its rel was the login marker.** `DiscoveryCache` treats `export-account-data` (a GET, offered
   only to logged-in callers) as proof that the token reached the auth API. Turning that rel into a
   POST would have signed every user out.

## Decision

1. **Split the two roles.** `GET auth/account` (`GetAccount`, rel `account`) is the account resource.
   It asks for a login only and returns what the account page shows: no user id, no phone number.
   The auth root advertises `account` to logged-in callers, and `DiscoveryCache` now looks for that
   rel. `Rels.Account` carries the warning `Rels.ExportAccountData` used to carry.
2. **The export is `POST auth/account/data-export` with `{ "password": "…" }`.** The handler checks
   the password with `UserManager.CheckPasswordAsync`, exactly like `DeleteAccount`. A missing or
   wrong password is a 400 and no data. The account resource advertises the rel
   `export-account-data` (POST), and it stays on offer while a deletion is scheduled. The response
   is a plain document with no links, sent with `Cache-Control: no-store`. It stays a query with
   inline validation: it reads, it does not change state.
3. **No download token.** The ticket suggested a POST that mints a short-lived one-time token for a
   GET download. That exists only because a link cannot carry a password. A form post can, and a
   form post may answer with a file. So `/account/export` is now a page with a password form, and
   the form posts to `POST /account/export/download`, which returns the file. There is no token to
   store, expire or replay. The form binding makes the framework check the antiforgery token.
   A failure sends the browser back to the page with a code from a closed list
   (`?error=invalid-password`); the page never prints the raw query value.
4. **The TMS leg stays on a login.** `GET /api/v1/translators/me/data-export` is unchanged. Reasons:
   - The TMS holds no password and has no channel to the auth API. ADR-0032 and ADR-0049 both
     refused to build one.
   - A proof the TMS could check offline would be a second kind of JWT signed with the auth key.
     The TMS accepts any JWT from that issuer with the right audience (`AddJwtBearer`, no `typ`
     check), so such a token would also pass as an access token. That needs its own design.
   - What the endpoint returns is the name and e-mail the token already carries as claims, the
     profile's creation date, and ids and counts of public game text rows. Never the texts.
   - Its rel is the TMS-side login marker (ADR-0040). A stricter policy signs everyone out.
   The user-facing download is the only place where both halves become one file, and it asks for
   the password before it calls the TMS. A refused password never reaches the TMS.
5. **Every attempt is logged, in three places.** The auth API logs success and refusal with the
   user id, the masked address, the IP and the user agent. The TMS logs the same for its half. The
   frontend route logs too, because the APIs see the frontend server's address on a normal download
   and only the frontend sees the browser's. A direct API call with a stolen token, the case the
   ticket is about, shows the caller's real address in the API log.

## Consequences

- A bearer token alone no longer yields the auth export. It still yields `GET auth/account`, which
  is what the account page shows anyway. The ticket accepts this: the export was the tidy file.
- The download route no longer answers with a problem body. Every auth-side failure is a redirect to
  the export page with a Polish message. ADR-0032's rule is unchanged: only the auth half can fail
  the download, and a failed TMS half gives `isComplete: false`.
- Password guessing through the export is bounded by `auth-endpoint-limit` (10 per minute per IP),
  the same budget that guards deletion and password change. There is no per-account lockout on any
  of them. That is a shared gap, not a new one.
- `account` is a new rel and the login marker moved to it. Frontend and auth API must ship together.
  The frontend's discovery cache lives in memory, so a deploy clears it.
- When SEC-11 (#689) lands, the second factor plugs into the same POST body. A cross-service
  step-up for the TMS half belongs to that spec, not here.

## Alternatives considered

- **Password on the existing GET (header or query).** A password in a URL ends up in logs. And the
  endpoint was the account resource, so the pages would have needed it on every view.
- **One-time download token.** More moving parts (a store, an expiry, replay rules) for no gain over
  a form post that returns the file.
- **Fresh login (`max_age` / `auth_time`) as the step-up.** It would cover both APIs with no new
  channel. But a token minted right after a normal login would pass without the password being asked
  for the export, and it changes the OIDC flow. Worth revisiting inside SEC-11.
- **Signed step-up proof checked by the TMS.** Rejected for now; see decision 4.
