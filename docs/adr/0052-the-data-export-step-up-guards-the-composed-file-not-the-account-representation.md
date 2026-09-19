# ADR-0052: The Data-Export Step-Up Guards the Composed File, Not the Account Representation

**Status:** Accepted
**Date:** 2026-09-20
**Decision-makers:** Solo maintainer (ticket #690, SEC-12)
**Related:** ADR-0032 (the export's TMS leg is composed by the frontend), ADR-0040
(authorization-aware links), ADR-0041 (no gateway — discovery is the contract surface),
ADR-0049 (the revocation window is the access-token lifetime), tickets #686 (SEC-08),
#689 (SEC-11, TOTP), #690

## Context

Every sensitive account action asks for the current password first: deletion, e-mail change,
password change. The GDPR data export asked for nothing — a bearer token was enough. It is the
action that produces a single tidy file, which is what turns an account takeover into a data set.

Two facts about this repo shaped the fix, and neither is visible from the ticket:

1. **`GET auth/account/data-export` is not only the export.** It is the account resource the
   "Moje konto" page renders, and its `self` link says so. The page needs it on every visit, so it
   cannot ask for a password.
2. **Its rel is a sign-in probe.** `DiscoveryCache` treats a missing **GET** `export-account-data`
   in an authenticated discovery document as proof that the token never reached the auth API, and
   signs the session out. `Rels.cs` says this in writing. Moving that rel to a POST, or dropping it,
   signs every logged-in user out on their next page load — and during a rolling deploy it would do
   so while the old frontend is still running.

The threat is also narrower than "somebody knows the password", which is the premise of the rest of
the SEC batch and what #689 (TOTP) exists for. A password step-up stops the caller who holds a
**token or a session but not the password**: a session ridden from the browser, or an access token
that was revoked minutes ago and still validates for its five-minute lifetime (ADR-0049, #686).

## Decision

**The step-up guards the composed Art. 15 file. The account representation stays open to a
logged-in caller.**

- **New `POST auth/account/data-export`** (`DownloadAccountData`, rel `download-account-data`). The
  body carries the current password; the handler verifies it with `CheckPasswordAsync`, exactly like
  `DeleteAccount`, and returns the payload only then. Both outcomes are written to the audit log.
- **`GET auth/account/data-export` is unchanged in shape, rel and method.** Its log line no longer
  says "GDPR data export completed" — it fired on every account page view, which is what made the
  audit log unable to answer who took a file. It now says "account data read", and the two export
  lines belong to the POST.
- **The frontend download route is a POST** at `/account/export/download`, fed by a
  "confirm your password" page at `/account/export`. The password is checked by the auth API, never
  by the frontend, so a frontend session alone is not enough.
- **The TMS contribution export keeps a plain login.** Its payload is the caller's own translator
  profile plus the ids of rows they submitted or approved, all of which the same session already
  sees in the TMS UI. Gating it would need the auth server to mint a step-up token that the TMS can
  verify — the cross-context back-channel ADR-0032 deliberately did not build — and its rel is the
  TMS sign-in probe in the same way, so a tighter policy signs every logged-in user out. The file is
  gated one layer up: the frontend fetches this endpoint only after the auth API accepted the
  password.
- **The audit line is written twice, on purpose.** The auth API proves the password check happened;
  the frontend's line carries the reader's real IP and user agent, because nothing forwards the
  client address between the two services. Both carry the user id and the masked address.

## Consequences

- **A token holder can still read the auth payload off the GET.** The account page renders those
  same values, so this is not a new disclosure — and the ticket says as much ("an attacker who is
  already inside the account can read most of these values off the account page anyway"). What now
  needs the password is the packaged file, which adds the TMS contribution list and the phone number
  and is the artefact worth stealing. Closing the GET as well means splitting it from the account
  representation, and that is only worth doing if a second API consumer ever appears.
- **Two rels now describe the same data.** `export-account-data` (GET) is the representation,
  `download-account-data` (POST) is the export. Rel names are a frozen contract (ADR-0041) and this
  is an addition, which is the cheap direction; both are offered to every logged-in caller, so the
  sign-in probe is untouched and the change is safe in either deploy order.
- **A wrong password does not count toward Identity's lockout**, mirroring `DeleteAccount`. The
  endpoint's rate limit is the only brake, which is enough for a caller who already holds a session.
- **The confirm page is one more click** before a download the user asked for. That is the intended
  cost, and it is the same cost the other three sensitive actions already charge.
- **When TOTP lands (#689)**, this POST is where the second factor goes for the export. Nothing in
  the shape has to change: it already has a request body and a place to refuse.

## Alternatives considered

- **A short-lived one-time download token minted by a POST** (the ticket's first option). It needs
  either server-side state, which Static SSR does not keep, or a Data-Protection payload that is not
  really one-time. The token would only carry the same proof the password already gives one request
  earlier, so it buys nothing here.
- **Gating the frontend route only.** It would stop a browser session thief but not a token holder,
  and the enforcement would live in the client rather than in the API that owns the data.
- **Renaming `export-account-data` to `account` and giving the export the old rel.** The honest
  naming, and rejected: it signs every logged-in user out during the deploy window, for a rename.
