# ADR-0054: Frontend Calls to the Auth API Are Metered on the Visitor's Address, Proven by a Per-Environment Shared Key

**Status:** Accepted (amended 2026-09-22 — see "Amendment: the TMS API uses the same key"; amended
2026-09-23 — see "Amendment: resend-confirmation now has a per-account budget")
**Date:** 2026-09-22
**Decision-makers:** Solo maintainer (ticket #819)
**Related:** AuthSystem.API (`Program.cs` rate-limit policies, `Services/RateLimiting`, `Settings`),
Frontend (`Infrastructure/HttpClients`, `Infrastructure/Auth`, `Settings`), AuthSystem.Contracts;
since #823 also TranslationSystem.API (`Program.cs`, `Services/RateLimiting`, `Settings`) and
Utilities/Hateoas.Abstractions (`FrontendCallerHeaders`);
ADR-0053 (the per-account confirmation budget, whose alternative D deferred this), ADR-0034 and
#399/#506 (forwarded-header trust pinned to Caddy's `/32`), ADR-0041 (no gateway);
TheKittySaver ADR-0044 (the same decision on its Adoption API, lifted here); tickets #813, #819, #823

## Context

Every per-address rate-limit policy in `AuthSystem.API/Program.cs` partitions on
`Connection.RemoteIpAddress`: `fixed-by-ip` (20/min, the endpoint group), `auth-endpoint-limit`
(10/min: the OpenIddict `/connect/*` endpoints, `auth/register`, `confirm-email`, `reset-password`,
`cancel-deletion`, `GET auth/account/data-export` and the four link-landing pages),
`change-email-limit` (3/h), `forgot-password-limit` and `resend-confirmation-limit` (3 per 15 min,
POST only) and `auth-page-limit` (10 POSTs per 15 min, keyed by route and address). Code facts that
shaped this decision:

- That address names the visitor only when the visitor talks to Caddy. `UseForwardedHeaders` honours
  `X-Forwarded-For` from Caddy's pinned `10.60.0.100/32` alone, with `ForwardLimit = 1`
  (`Program.cs`, `compose.hetzner.yaml`; the parity stack trusts the `10.60.0.0/24` subnet), and
  Caddy writes the address of the peer it saw.
- The frontend renders server-side and calls the auth API through the public origin
  (`AuthSystem__BaseUrl: https://${DOMAIN_AUTH}/`). Inside the stack that hostname is a network
  alias of the same Caddy, which proxies it to `auth-api:8080`. For those calls Caddy's peer is the
  frontend container, so the auth API resolves **every** frontend call to the frontend's address:
  one bucket for all logged-in users. Ten account page views in one minute, or ten logins across
  all users, and the eleventh call was a 429 for everybody.
- The frontend reaches the auth API over three outgoing paths, not one: the typed `IAuthSystemClient`
  (the account representation and the account POSTs), `ITokenEndpointClient` (the `refresh_token`
  grant, on every near-expiry request) and the OIDC handler's own back-channel `HttpClient` (the code
  exchange at `/connect/token`, `/connect/userinfo`, discovery and JWKS). None of them sent anything
  beyond `Accept` and `Authorization`.
- #813 (ADR-0053) took the three password-confirmation endpoints off the per-address policies and
  gave them a per-account budget, and deferred the shared bucket itself as its alternative D.
- **`X-Forwarded-For` cannot carry the visitor from the frontend.** Caddy replaces an incoming
  `X-Forwarded-For` from a peer outside `trusted_proxies` with that peer's address, and neither
  Caddyfile configures `trusted_proxies`. Any other request header passes through unchanged.
- **The frontend has no stable address to trust.** Both compose files pin only Caddy's address;
  the frontend container draws from the dynamic pool.
- **A per-user key is impossible in the limiter.** `UseRateLimiter` runs before `UseAuthentication`
  on purpose (#347: OpenIddict rejects junk `/connect/*` traffic during authentication, so a limiter
  after it would never count it), and the code exchange at `/connect/token` is anonymous anyway.
- **The frontend has no credential of its own.** It is a public OIDC client (PKCE, no secret).
- **The box already fails loudly on a missing value.** `scripts/hetzner/deploy.sh` runs
  `compose config --quiet` before it pulls, migrates or recreates anything, and `deploy.yml`'s
  rollback puts the last green compose file back and re-runs the previous tag.
- TheKittySaver hit the identical problem on its Adoption API nine days earlier and settled it in its
  ADR-0044. Every TMS-side pattern is lifted from there (CLAUDE.md), and this one arrives with its
  tests.

## Decision

### 1. A frontend call is metered on the visitor's address, in the bucket a direct caller gets

The frontend forwards the address its own forwarded-headers step resolved
(`HttpContext.Connection.RemoteIpAddress`, the visitor as Caddy saw them) on every auth API call it
makes inside a request, anonymous and signed-in alike. The auth API uses that address, in the same
string form as a direct caller's, as the partition key. Budgets do not change.

### 2. The forwarded address counts only with the frontend's key

Two request headers, named once in `AuthSystem.Contracts` so both apps compile against the same
strings (moved to `Hateoas.Abstractions` by #823, see the amendment):

| Header | Value |
|---|---|
| `X-LOTRO-Frontend-Key` | the environment's shared key |
| `X-LOTRO-Client-Address` | the visitor's address |

`RateLimitPartitionKeyResolver` compares SHA-256 digests of the presented and the configured key
with `CryptographicOperations.FixedTimeEquals`, so neither the timing nor the length of the key
leaks. A call is metered on the forwarded address only when a key is configured, exactly one key
value matches and exactly one address value parses. In every other case — no key configured, header
missing, wrong key, missing, repeated or unparseable address — it is metered on
`Connection.RemoteIpAddress`, exactly as before. A caller without the key cannot choose a bucket,
and a broken header never widens anything.

The key decides the bucket and nothing else. It opens no endpoint, carries no identity, exempts from
no budget and leaves `Connection.RemoteIpAddress` alone: logs, the audit lines and
`AuthorizationLoggingMiddleware` keep seeing the frontend's address. It is not machine-to-machine
authentication.

### 3. The three policies the frontend reaches key through the resolver; the browser-facing pages do not

`fixed-by-ip` (the discovery root the frontend fetches), `auth-endpoint-limit` and
`change-email-limit` take their key from the resolver. `auth-page-limit`, `forgot-password-limit`
and `resend-confirmation-limit` stay on `Connection.RemoteIpAddress`. The frontend never posts to a
Razor page, so for those the connection address already is the client, and honouring the key there
would only enlarge what a leaked key can do: the login form is the one place a password can be
guessed in production (#692), and `resend-confirmation` has no per-account budget behind it, so a key
would turn three mails per quarter of an hour per address into an unbounded flood of one inbox.
`POST auth/register` leaves `auth-endpoint-limit` for the same reason: it mails a caller-typed
address, the account it would brake does not exist yet, and the frontend never calls it (the
registration form is a Razor page), so it gets its own `register-limit` with `auth-endpoint-limit`'s
numbers, keyed on the connection. A test pins that a POST to the login page, `auth/register`,
`auth/forgot-password` or `auth/resend-email-confirmation` carrying the key and an address is still
metered on the connection.

### 4. One handler on all three outgoing paths

`FrontendCallerDelegatingHandler` adds both headers when a key is configured and a request is
current. It sits in the `IAuthSystemClient` and `ITokenEndpointClient` pipelines through
`AddHttpMessageHandler`, and it wraps the OIDC handler's `BackchannelHttpHandler`, so the code
exchange and the userinfo call carry the visitor too. The address-forwarding step is orthogonal to
transport, so a back-channel handler configured elsewhere is wrapped, never replaced.

### 5. One key per environment, from the box `.env`, read by both services

`FRONTEND_CALLER_KEY` in `/opt/lotro/.env`, generated by the owner with `openssl rand -base64 32`,
a different value per box. `compose.hetzner.yaml` passes it to `auth-api` as `FrontendCaller__Key`
and to `frontend` as `AuthSystem__CallerKey`; one compose run starts both, so the two copies cannot
drift. The parity stack reads the same pair from `.env.prod`, where `gen-openiddict-keys.{sh,ps1}`
mint it next to the OpenIddict secrets. Rotating it is a new value plus a redeploy. For the seconds
in which only one container has the new key, frontend calls fall back to the frontend's own bucket —
harmless. Since #823 the same line also feeds `tms-api` (`FrontendCaller__Key`) and the frontend's
`TranslationSystem__CallerKey`: one key per box, three services (see the amendment).

### 6. A missing key fails the deploy and the boot

- **Compose:** both services read `${FRONTEND_CALLER_KEY:?…}`, so `deploy.sh` stops at
  `compose config --quiet` before it touches a container, and the rollback keeps the running
  release serving.
- **Apps:** outside Development and Testing both refuse to start without a key, and a key shorter
  than 32 characters fails options validation wherever one is set. Development and Testing may
  leave it empty: their limiter is off, the frontend then sends neither header, and the auth API
  ignores a forwarded address. Since #823 the TMS API follows the same two rules.

A missing key would quietly bring back the one shared bucket, and nothing but real traffic would
notice. Operator order: the key goes into the staging `.env` before the change merges (CD deploys
staging on merge) and into the prod `.env` before the change is promoted.

### 7. A call outside a request stays on the frontend's own bucket

The frontend forwards only an address it has. A call with no current `HttpContext` sends neither
header and is metered on the frontend's address, as today. No such caller exists: the frontend runs
no hosted service, and its discovery cache resolves inline in the request.

## Consequences

### Positive

- Two users logging in through the frontend within a minute no longer 429 each other; one user's
  page views cannot refuse another's e-mail change.
- The e-mail change mail budget belongs to the real client address, and no auth API policy is keyed
  on an address that is not the client's — the three acceptance criteria of #819.
- Direct callers see no change, and every budget keeps its size.
- Nothing changes in Caddy, and the apps still trust one `/32` for forwarded headers.
- A box without the key fails at deploy time instead of degrading in silence.

### Negative / Accepted Trade-offs

- **One more secret per environment.** Accepted: one line per box, no expiry, and rotation is an
  `.env` edit plus a redeploy.
- **A leaked key lets its holder choose buckets on the three policies the frontend reaches** — and
  nothing more. The login, registration, reset and resend pages and the registration endpoint keep
  the connection's address as their key, and Identity's lockout (five failures, per account) and the
  per-account budgets of #692 and #813 do not read the key. Accepted: rotating the key ends it.
- **A cold discovery cache is fetched once per caller, not once per key.** The frontend's discovery
  cache never makes the API call inside a `HybridCache` factory: it reads the cache, calls the API in
  the request on a miss, and stores only a good answer (#825). So every fetch carries its own
  caller's bearer and address. The cost is that callers who miss the cache at the same moment each
  make their own call instead of waiting for one. Accepted: the window is a cold cache only, and the
  payload is tiny. Until #825 the call ran inside the factory, and for a token that can be cancelled
  (the export route passes `RequestAborted`) `HybridCache` runs the factory on the thread pool without
  the request's context, so that GET carried neither the caller headers nor the bearer.
- **The key crosses the stack network in plain HTTP** between Caddy and the auth API — the same
  hop every bearer token already takes. Accepted.
- **People behind one NAT still share a bucket**, now through the frontend too. Accepted, as
  `auth-page-limit` already accepts for the login form (#692).
- **Logs keep the frontend's address for frontend calls.** Accepted: the key picks a bucket; making
  it redefine `RemoteIpAddress` is a wider trust change than this problem needs. The frontend's
  own audit lines already carry the reader's real address (#690).
- **The merge that ships this reds the staging deploy on a box without the key.** Accepted: that is
  decision 6 working, and the rollback keeps the old release serving.
- **The TMS API has the same shared bucket** (`fixed-by-ip`, 100/min, every translator page load).
  Out of scope here; #823 applies this recipe there (see the amendment below).

## Alternatives Considered

### A. Forward `X-Forwarded-For` from the frontend and trust a second hop

The ticket's first option: the frontend appends `X-Forwarded-For`, Caddy keeps it, the auth API
runs `ForwardLimit = 2` with the frontend's network in `KnownIPNetworks`. Rejected. Caddy drops an
incoming `X-Forwarded-For` from a peer outside `trusted_proxies`, so both Caddyfiles would name the
frontend as a trusted proxy; the frontend has no static address, so both compose files would pin
one; and the auth API would honour forwarded headers from a container address — the hole #506
closed by narrowing trust to Caddy's `/32`. The key needs none of that.

### B. Move the affected budgets into the handlers as per-account throttles

The ticket's second option. Rejected for the endpoints that matter: the code exchange at
`/connect/token` and `/connect/userinfo` are OpenIddict's, anonymous until the exchange succeeds,
and have no handler of ours; the account GET needs no brake per account at all. The e-mail change
send could carry one, but that would leave the two acceptance criteria about logins unmet.

### C. Exempt proven frontend calls from the auth API limiter

Rejected. The frontend has no limiter of its own here, so a runaway loop in the frontend would have
no ceiling at all. Once the address is forwarded, the per-address budget costs nothing extra.

### D. A separate, larger partition for the frontend's address

Rejected. It raises the ceiling instead of removing the sharing, and its trust hangs on a container
address.

### E. Key every per-address policy through the resolver, the page policies included

One rule instead of two. Rejected. The frontend never calls a Razor page, so the page policies gain
nothing, and the key would become a bypass for the login form's only per-address brake and for the
`resend-confirmation` mail budget, which has no per-account twin: a leaked key would buy free password
spraying and an unbounded flood of one inbox, for a benefit of zero. TheKittySaver could key every
policy because its API has no login surface; this one has.

### F. Sign the address with an HMAC and a timestamp instead of sending a static key

Rejected. A replayed header only meters the replayer on that visitor's address, which a static key
already bounds. Signing adds code and clock handling for no protection this threat needs.

### G. Rewrite `Connection.RemoteIpAddress` for proven frontend calls

Rejected for now. It would put the visitor into the auth API's logs too, but it changes the address
every consumer sees — a trust change well beyond the limiter.

## Implementation Notes

- Contracts: `AuthSystem.Contracts/Common/FrontendCallerHeaders.cs` — **new**; the two header names.
  When the TMS follow-up needs them, they move to `Utilities/LotroKoniecDev.Hateoas.Abstractions`,
  which every Contracts project and the frontend already reference and which hosts the vendor media
  type for the same reason — never a `TranslationSystem` → `AuthSystem.Contracts` reference. (Moved
  by #823.)
- Auth API:
  - `Settings/FrontendCallerSettings.cs` + `Settings/FrontendCallerSettingsValidator.cs` — **new**;
    `FrontendCaller:Key`, required outside Development/Testing, at least 32 characters when set.
  - `Services/RateLimiting/RateLimitPartitionKeyResolver.cs` — **new**; the digest comparison and
    the choice between the forwarded address and `Connection.RemoteIpAddress`.
  - `ApiDependencyInjection.cs` registers the settings and the resolver; the three back-channel
    policies in `Program.cs` partition through it, the page policies and `register-limit` through
    `ConnectionAddress`; `Features/Auth/RegisterUser.cs` moves to `register-limit`.
- Frontend:
  - `Settings/AuthSystemSettings.cs` (+ `CallerKey`) and `Settings/AuthSystemSettingsValidator.cs`.
  - `Infrastructure/HttpClients/FrontendCallerDelegatingHandler.cs` — **new**; on the two typed
    clients (`HttpClientsDependencyInjectionExtensions`, `AuthenticationDependencyInjectionExtensions`)
    and as the OIDC `BackchannelHttpHandler`.
- Deploy and docs: `compose.hetzner.yaml`, `compose.prod.yaml`, `.env.hetzner.example`,
  `.env.prod.example`, `scripts/gen-openiddict-keys.{sh,ps1}`, `scripts/up-prod.{sh,ps1}`,
  `docs/deployment/runbook.md` (env-var matrix, secrets table, consistency rule), `docs/API.md` §2.4,
  ADR-0053 (its alternative D and the "remaining policies" trade-off now point here).
- Tests:
  - Auth API: `RateLimitPartitionKeyResolverTests` (unit: every call short of a proven visitor);
    `FrontendCallerKeyTests` (integration, forced-on limiter: two users behind one address no longer
    share `/connect/token` nor the account GET, the e-mail change budget per visitor, a direct
    caller unchanged, the connection's own bucket for an unproven call, the discovery root per
    visitor, and the key-blind endpoints — the login page, `auth/register`, `auth/forgot-password`,
    `auth/resend-email-confirmation` — still metered on the connection whatever headers they carry);
    `FrontendCallerSettingsValidatorTests`.
  - Frontend: `FrontendCallerDelegatingHandlerTests` (the handler alone, and the three paths through
    the real registrations: the typed client under a retry, the token client, the OIDC back-channel),
    `AuthSystemSettingsValidatorTests`.

## Amendment: the TMS API uses the same key (2026-09-22, #823)

The TMS API had the same shared bucket. Its one policy, `fixed-by-ip` (100 requests per minute),
was keyed on `Connection.RemoteIpAddress`, and every translator page load reaches it from the
frontend container. At a few calls per page, 30–50 page views a minute across the whole site would
have put every page on the 429 copy. The decision above now covers the TMS API too:

- **One copy of the header names.** `FrontendCallerHeaders` moved from `AuthSystem.Contracts/Common`
  to `Utilities/LotroKoniecDev.Hateoas.Abstractions`, which both Contracts projects and the frontend
  already reference. The TMS never references `AuthSystem.Contracts`.
- **The TMS API has its own copy of the resolver**: `Services/RateLimiting/RateLimitPartitionKeyResolver`
  and `Settings/FrontendCallerSettings` with its validator, under the rules of §2 and §6. The two APIs
  share the header names, not code, and each copy has its own unit suite.
- **`fixed-by-ip` keys through the resolver.** §3's reason to keep some auth policies on the
  connection does not apply here: the TMS API has no login form and sends no mail, so a leaked key
  only lets its holder pick a bucket on 100 requests per minute. A direct caller, such as the CLI's
  translation-file download, stays on its own address.
- **One key per box, not one per API.** Compose passes the same `FRONTEND_CALLER_KEY` to tms-api as
  `FrontendCaller__Key` and to the frontend as `TranslationSystem__CallerKey`. The frontend keeps one
  key per API in its settings (`AuthSystemSettings.CallerKey`, `TranslationSystemSettings.CallerKey`),
  and `FrontendCallerDelegatingHandler` takes the key of the API it calls, so the two keys could be
  split later without a code change. It sits on `ITranslationSystemClient` outside the resilience
  handler, as on the auth clients. No box needs a new line: it is the line #819 already requires.
- **A test host can force the TMS limiter on** with `RateLimiting:ForceEnable`, as on the auth API.
  The policy metadata is now always on the endpoint group, and `UseRateLimiter` is the one switch.
- The TMS limiter still runs after authentication, so a request refused with 401 is not counted.
  That is unchanged and not part of this amendment.

Tests: `TranslationSystem.API.Tests.Unit` — `RateLimitPartitionKeyResolverTests`,
`FrontendCallerSettingsValidatorTests`; `TranslationSystem.API.Tests.Integration` —
`FrontendCallerKeyTests` (forced-on limiter: two translators behind the frontend, the container's own
bucket, one visitor keeps one bucket whichever way the call arrives, calls short of a proven visitor);
`Frontend.Tests.Unit` — `FrontendCallerDelegatingHandlerTests` (the TMS client through the real
pipeline under a retry, with its own key) and `TranslationSystemSettingsValidatorTests`.

## Amendment: resend-confirmation now has a per-account budget (2026-09-23, #793)

§3 and alternative E argue that `resend-confirmation` "has no per-account budget behind it", so a key
honoured there would turn three mails per quarter of an hour per address into an unbounded flood of
one inbox. That is no longer true: ADR-0055 gave the resend a budget of 3 sends per 15 minutes per
unconfirmed account, taken in the handler. A leaked key on that policy would now buy a bigger IP
budget, not an unbounded flood.

The decision itself does not change. The frontend never calls the resend, on the page or on
`auth/resend-email-confirmation`, so honouring the key there would still gain nothing and would only
widen what a leaked key can do. `resend-confirmation-limit` stays on the connection's own address.

## References

- Ticket #819; #813 / ADR-0053 (alternative D); #399, #506 / ADR-0034 (the `/32` trust); #347 (the
  limiter runs before authentication); #692 (`auth-page-limit`).
- TheKittySaver ADR-0044 and koniecdev/TheKittySaver#791 — the same decision on the Adoption API.
- Caddy `reverse_proxy` header defaults and `trusted_proxies`:
  https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#defaults
- `CryptographicOperations.FixedTimeEquals`:
  https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.fixedtimeequals
