# ADR-0052: The Data-Export Step-Up Guards the Composed File, Not the Account Representation

**Status:** Accepted
**Date:** 2026-09-20
**Decision-makers:** Solo maintainer (ticket #690, SEC-12)
**Related:** ADR-0032 (the export's TMS leg is composed by the frontend), ADR-0040
(authorization-aware links), ADR-0041 (no gateway — discovery is the contract surface),
ADR-0049 (the revocation window is the access-token lifetime), tickets #686 (SEC-08),
#689 (SEC-11, TOTP), #690, #813

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
- **`GET auth/account/data-export` keeps its route, rel and method**, and is trimmed to what the
  account page actually renders. The contact details — today the phone number — move to the POST, so
  the password buys the reader something the page does not already hand over. Both endpoints read
  through one `AccountDataExportReader`, so they cannot drift apart. Its log line no longer says
  "GDPR data export completed" either: it fired on every account page view, which is what made the
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

- **A token holder can still read the account representation off the GET, so the ticket's first
  acceptance criterion is met in spirit and not to the letter.** The maintainer accepted this
  deviation on 2026-09-20, before #814 was merged.
  The GET now returns exactly what the account page renders, and a caller holding a token can render
  that page anyway — the ticket says as much ("an attacker who is already inside the account can read
  most of these values off the account page anyway"). What needs the password is the packaged file:
  the contact details plus the TMS contribution list, which is the artefact worth stealing. Note what
  this does **not** stop: the SEC batch's own premise is an attacker who knows the password, and
  against that reader a password step-up is worth nothing. It stops the caller who holds a token or a
  session but not the password. The second factor of #689 is what raises that bar, and this endpoint
  is where it plugs in.
  The phone number is empty for every account today, because nothing collects one — so the tightening
  above is structural, not a leak closed. Closing the GET completely means splitting it from the
  account representation, and that is only worth doing if a second API consumer ever appears.
- **Two rels describe the account's data.** `export-account-data` (GET) is the representation,
  `download-account-data` (POST) is the export. Rel names are a frozen contract (ADR-0041) and this is
  an addition, which is the cheap direction, and the GET rel keeps its sign-in-probe duty untouched.
  The new rel is advertised **on the account resource only**, next to `change-password` and
  `delete-account`, and deliberately not in the discovery document: discovery is cached for a day
  under one shared key, so a document fetched while an older auth server was still answering would
  keep the export broken long after the deploy finished, and signing out would not clear it. Read from
  the representation, the client always sees what the server offers right now, and the account page's
  button and the download route can never disagree.
- **The export is not withdrawn by a scheduled deletion, but the window is short.** Taking a copy of
  your own data is a right, so the representation advertises it in the deletion-scheduled branch too,
  where every other action is withdrawn, and the endpoint serves it there. In practice that reaches
  only a caller whose access token was issued before the deletion was scheduled, for the few minutes
  it stays valid (ADR-0049): scheduling locks the account and revokes its sessions, so nobody can log
  in during the 14-day window to ask for the file. Offering the export inside the window — from the
  cancellation e-mail, for one — is a product decision this ADR does not make.
- **A wrong password does not count toward Identity's lockout**, mirroring `DeleteAccount`. When this
  ADR was written, the only brake behind it was `auth-endpoint-limit`, which partitions on the remote
  address — and every call from the frontend arrives from the frontend itself, so it was one
  10-per-minute bucket shared by every logged-in user. #813 replaced it for this endpoint with a
  per-account budget, `PasswordConfirmationThrottle`; ADR-0053 holds that decision and the reason the
  lockout stays out of it.
- **The confirm page is one more click** before a download the user asked for. That is the intended
  cost, and it is the same cost the other three sensitive actions already charge.
- **A refused attempt shows a sentence and a "try again" link, not the form.** A successful download
  answers with a file, so the browser stays on the page it posted from. A form next to a "wrong
  password" banner would hand the file over under that banner. The Frontend is Static SSR and ships
  no script of its own, so the page cannot clear itself; one more click after a typo is the price of
  never showing both at once.
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
