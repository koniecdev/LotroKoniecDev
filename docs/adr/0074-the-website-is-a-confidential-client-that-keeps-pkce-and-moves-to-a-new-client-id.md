# ADR-0074: The Website Is a Confidential Client That Keeps PKCE, and It Moves to a New Client Id

**Status:** Accepted
**Date:** 2026-11-14
**Decision-makers:** Solo maintainer (ticket #1033, SEC-37)
**Related:** AuthSystem.API (`Extensions/DatabaseSeederExtensions.cs`, `Settings/OpenIddictSettings.cs`),
Frontend (`Infrastructure/Auth/AuthenticationDependencyInjectionExtensions.cs`,
`Infrastructure/Auth/TokenRefresh/TokenEndpointClient.cs`, `Settings/AuthSystemSettings.cs`),
SharedKernel `AuthConstants.ClientIds`, `compose.hetzner.yaml`, `compose.prod.yaml`; ADR-0008 §3,
ADR-0054, ADR-0060, ADR-0061, ADR-0067; tickets #1033, #830, #1062

## Context

The website renders every page on our server, and its tokens never reach the browser: they sit
encrypted in its session cookie. So it can keep a secret. It was still registered with the sign-in
server as a **public** client, `lotrokoniecdev-web`: it names itself with its client id and proves
nothing else. The id is public, because the browser shows it in the sign-in address.

PKCE ties a sign-in code to the sign-in that asked for it, so a stolen code is useless to anyone who
did not start that sign-in. But PKCE does not stop an attacker from starting a sign-in of their own
as `lotrokoniecdev-web`, with their own PKCE values, and finishing it from their own machine. A raw
refresh token works from any machine too. RFC 9700 says a client that can authenticate should do so.

No leak path is known today. The request log redacts `code`, `refresh_token` and `client_secret`
(`SensitiveDataRedactor`), OpenIddict redacts its own request log, and the website logs only the two
OAuth error fields. So this is hardening.

OpenIddict 7.8.0 allows only two client types, and its `ValidateClientType` handler refuses both
mixes at the token and revocation endpoints: a public client that sends a `client_secret` gets
`invalid_client`, and so does a confidential client that sends none. One registration therefore
cannot serve a website that sends a secret and one that does not.

The seeder only creates a client that is missing and never updates a row (runbook, "Reseed traps"). A
deploy recreates all three app containers in one `compose up`, and the old ones are gone before the
new ones start (`scripts/hetzner/deploy.sh`). A rollback runs the same script with the previous tag,
and the previous release's seeder creates any client it knows that is missing.

## Decision

### 1. The website authenticates with a client secret, and PKCE stays

The website sends its secret as a `client_secret` form field (`client_secret_post`, OpenIddict's
default) on the code exchange, the refresh and the revoke. The two checks answer different questions:

- The secret proves the call comes from our server, so a code or a refresh token of this client is
  useless anywhere else.
- PKCE proves the code belongs to this sign-in. Without it, an attacker could paste a victim's code
  into our own callback, and our server would redeem it, secret included.

PKCE is required for every client by the server-wide `RequireProofKeyForCodeExchange()`, so nothing
changes there. The OIDC handler keeps `UsePkce = true` and `ResponseMode.Query`.

### 2. The website moves to a new client id, `lotrokoniecdev-frontend`

The seeder creates `lotrokoniecdev-frontend` next to the old client: confidential, with the secret,
the same permissions and the same redirect and post-logout addresses, and no `client_credentials`.
The website's `AuthSystem:ClientId` changes to it. The old client stays as it is.

Switching `lotrokoniecdev-web` in place would need an update path in the create-only seeder, and every
existing database (each box and each local one) would keep the public row until someone reseeds by
hand. A new id needs neither: the seeder creates it on the first start.

Cost: every live session ends once. Its refresh token belongs to the old client, and OpenIddict
refuses a token presented by another client (`invalid_grant`, which the website treats as final,
#1062). The website then revokes that token as the new client, and OpenIddict answers 200 but leaves
it valid, because one client may not revoke another's token (RFC 7009 lets it ignore the request), so
the website logs nothing. Such a token stays valid on the server until it expires, at most 9 hours
later, unless an explicit sign-out ends the session through the ID token hint. A sign-in that started
before the roll and returns after it fails once at the code exchange (its code belongs to the old
client) and works when the user tries again.

### 3. The old public client stays for one release, then a follow-up removes it

No roll and no rollback needs the old client: a deploy never runs the new sign-in server next to the
old website, and a rollback's seeder creates the old client again anyway. It stays because #1033
asks for a two-step rollout, because a rollback of this release then keeps the sessions that existed
before it, and because this change does not have to move the many tests that still use it.

Until it is removed, the old public client works as before: it still accepts a new sign-in without a
secret, and a refresh token issued to it still renews without one. So the hardening is partial until
the follow-up lands. The follow-up deletes the client through OpenIddict's application manager in
seeder code that runs on every start (which also deletes its tokens and authorizations), never with a
one-off SQL or a migration: a rollback to a release that still seeds it would bring it back.

### 4. One secret per box, read by two services, required outside Development and Testing

`FRONTEND_CLIENT_SECRET` in the box `.env` feeds `OpenIddict__FrontendClientSecret` on auth-api and
`AuthSystem__ClientSecret` on the website. Both compose files guard it with `:?`, so a box without the
line fails `deploy.sh`'s first gate and the old release keeps serving (the ADR-0054 §6 pattern). Both
apps refuse to boot outside Development and Testing when it is missing or shorter than 32 characters,
like `OpenIddict__ApiClientSecret` (ADR-0008 §3: a missing client secret is a misconfiguration that
fails silently). The key scripts mint it next to the other keys.

OpenIddict stores only a hash of the secret, and the seeder writes it only when it creates the
client. So the value must not change after the first deploy: a changed value breaks every sign-in,
and no automated gate sees it, because smoke's token leg uses `lotrokoniecdev-api`. Rotation follows
the runbook's reseed path.

The secret travels only to the token and revocation endpoints, and no client follows a redirect. The
refresh and the revoke pass `SameOriginDelegatingHandler`. The code exchange goes through the OIDC
back-channel, which has no origin check: its addresses come from the sign-in server's own metadata,
and it already carries the code there (the owner's ruling on #830, recorded in ADR-0067's context).

## Consequences

### Positive

- A code or refresh token of the website's client cannot be redeemed without the box's secret, and a
  sign-in started as this client cannot be finished from another machine.
- A wrong secret or a refresh token tried without one is refused with `invalid_client`, and the
  sign-in server logs the refused call as a Warning (`AuthorizationLoggingMiddleware`).
- No migration: a client is one row the seeder writes, and an older release ignores a row it does not
  know.

### Negative / Accepted Trade-offs

- Every signed-in user is signed out once when a box first runs this release, and a form posted in that
  request is not saved. The owner picks a quiet time for the prod promotion.
- Each live session logs one refused refresh on the website and the sign-in server's refusal line on
  the day of the deploy. Its refresh token stays valid on the server, unused, for up to 9 hours (above).
- The owner adds the `.env` line to each box before the merge; a session cannot write to the boxes.
- A changed secret breaks sign-in with every gate green (above). The runbook's reseed table says so.
- Until the follow-up, the old public client is the same gap as before.

## Alternatives Considered

### A. Switch `lotrokoniecdev-web` to confidential in place

Rejected: it needs an update path in the create-only seeder, every existing database keeps the public
row until a reseed, and an older image after a rollback would send no secret to a client that now
needs one.

### B. Remove the old public client in the same release

On merits it closes the gap at once and needs no follow-up, and a rollback would still work, because
the older seeder creates the client again. Not taken: #1033 asks for a two-step rollout, a rollback
would lose the sessions from before the release, and it would move every test that uses the old
client into this change. The follow-up can run as soon as the owner wants it.

### C. Use `FRONTEND_CALLER_KEY` as the client secret

Rejected: the translation API holds that key too and would then hold a credential that redeems refresh
tokens, the website sends it as a header on every call, and it rotates by editing `.env`, while a
seeded client secret needs a reseed.

### D. The seeder updates the stored secret on every start

It would remove the reseed trap of decision 4. Not here: done for one field of one client, the seeder
would have two rules, and done for every client and every address it is its own change.

### E. A startup check that the configured secret matches the stored one

It would turn the trap of decision 4 into a failed deploy instead of a silent outage, for one
password-hash check per start. Not taken here: #1033 keeps the create-if-missing seeder as it is, the
same trap exists for the other seeded values, and the runbook's reseed table and the after-deploy
sign-in check cover it. It is a question for the owner, raised in the PR.

### F. Sender-constrained tokens: certificate-bound refresh tokens, DPoP, `private_key_jwt`

OpenIddict 7.8.0 can bind refresh tokens to a client certificate, but TLS ends at Caddy, the
certificate expires, and its key would sit on the same box as the secret; OpenIddict itself treats
client authentication as the sender constraint of a confidential client. DPoP is not in the 7.8.0
server, and the ASP.NET Core OIDC handler has no `private_key_jwt`, so both would need our own
protocol code (ADR-0067 decision 5).

## References

- RFC 9700 (OAuth 2.0 Security Best Current Practice), §2.5 client authentication.
- OpenIddict 7.8.0 `OpenIddictServerHandlers.ValidateClientType` and `ValidateClientSecret`.
- Ticket #1033; #830 (the OIDC back-channel's origin); #1062 (a refused renewal is final).
