# ADR-0040: Authorization-aware HATEOAS link emission, and an anonymous TMS service document

**Status:** Accepted
**Date:** 2026-08-05
**Decision-makers:** Solo maintainer
**Related:** #608 (the ticket), #309 (anonymous translation browsing), #153 (link-driven frontend affordances), ADR-0031 (deletion grace window — the auth link set this must not disturb), `LinkFactory`, `DiscoveryLinkFactory` (both systems), `DiscoveryCache` (frontend)

## Context

The TMS advertised one link — `self` — from a root that required a bearer token. Two consequences,
both load-bearing:

1. **Nothing was discoverable.** `TranslationSystem.Contracts/Hateoas/Rels.cs` carried actions
   (`upsert`, `approve`, `register`, `delete`) and pagination, but no vocabulary for entry points. So
   every client hardcodes paths: the frontend six of them, the CLI
   `api/v1/translation-files/pl` in `TranslationFileDownloader`.
2. **The one client that most needs discovery could not reach it.** Three TMS endpoints are
   deliberately anonymous — `GetTranslationFile` (the CLI's download, #309-era decision),
   `GetPublicProgress` (the public home page) and `ListTranslations`. An unauthenticated client had
   no way to learn about any of them.

The frontend was already written against the fixed shape: `DiscoveryCache` keys its TMS entry by auth
state precisely because "the API tailors its HATEOAS link set per role". The API never delivered that
tailoring.

The obvious fix — branch on roles inside `DiscoveryLinkFactory` — restates each endpoint's
authorization rules in a second place. Those two places then drift, and a HATEOAS link that lies is
worse than no link: the client renders an affordance and the server answers 403.

The complication is blast radius. `LinkFactory` lives in the shared `Utilities/LotroKoniecDev.Hateoas`
project and serves **both** APIs. Auth discovery works correctly today and the frontend depends on it
hard: `DiscoveryCache` throws `AuthenticatedLinksDegradedException` and force-signs-out the session
when an authenticated caller does not get `export-account-data` back. A regression there silently
breaks the whole account section.

## Decision

### 1. A link is emitted only when the caller would be allowed to follow it

`ILinkFactory.CreateAsync` resolves the target endpoint's URI as before, then replays ASP.NET's own
authorization decision for that endpoint against the current request's caller. The endpoint's policy
stays the single source of truth; no link factory restates a role rule.

The evaluation mirrors `AuthorizationMiddleware` step for step:

1. Find the target `Endpoint` via `IEndpointAddressScheme<string>` — the same address scheme
   `LinkGenerator.GetUriByName` just used, so it is a dictionary lookup on the very endpoint whose
   URI was generated, not a second and possibly divergent match.
2. `IAllowAnonymous` metadata short-circuits to allowed, exactly as the middleware does. **The order
   matters and is not cosmetic:** `AllowAnonymousAttribute` does not implement `IAuthorizeData`, so
   under an authorized-by-default app an anonymous endpoint *still* combines to the fallback policy
   (verified against .NET 10 — a `MapGet(...).AllowAnonymous()` yields
   `DenyAnonymousAuthorizationRequirement` from `CombineAsync`). Evaluate the policy first and every
   public endpoint's link disappears for the guests it exists for.
3. `AuthorizationPolicy.CombineAsync(provider, authorizeData, policies)` folds the endpoint's
   `IAuthorizeData` with the application's **fallback** policy — verified: an endpoint with no
   authorization metadata at all combines to the fallback, not to `null`. This is what makes an
   authorized-by-default API (the TMS sets a `RequireAuthenticatedUser` fallback) fail closed for an
   endpoint that carries no metadata of its own.
4. A null policy means allowed. Otherwise `IAuthorizationService.AuthorizeAsync(user, null,
   policy.Requirements)` decides.

**Rejected: hand-rolled role checks in each discovery factory.** Simpler, and wrong for exactly the
case the tests now pin — a correctly-signed token whose role is neither `Admin` nor `Translator`
clears `RequireAuthenticatedUser` but not the role policies. A hand-written `isAuthenticated` branch
would have advertised the translator surface to it.

**Consequence accepted: the whole link-building chain became async.** `IAuthorizationService` is
asynchronous and blocking on it is not an option, so `Create` → `CreateAsync`, every aggregate/
pagination/discovery factory method gained an `Async` suffix, and `HateoasResults.Ok`'s attacher went
from `Action<T>` to `Func<T, ValueTask>`. Seven endpoint call sites across both APIs. The delegate is
still only invoked when the client asked for the HATEOAS media type, so plain-JSON callers pay
nothing.

**Consequence accepted: `ILinkFactory` is now scoped, not singleton** — it depends on the scoped
`IAuthorizationService`. DI validation (`ValidateScopes` + `ValidateOnBuild`, #572) would have caught
the captive dependency at startup; the registration was changed deliberately instead.

No per-request caching of the resolved policy. `CombineAsync` builds a small object and the
requirements in play are role/claim checks that complete synchronously; a discovery document is ten
links. Caching is available later (`IAuthorizationPolicyProvider.AllowsCachingPolicies` is the same
guard the framework uses) if a profile ever justifies it.

### 2. The TMS root is anonymous; its contents are claims-aware

`Discovery` gets `.AllowAnonymous()`. This is the one deliberate hole in the authorized-by-default
house rule, and it is safe for a specific reason: **the endpoints advertised to an anonymous caller
are themselves anonymous**, so the document leaks nothing the caller could not already reach. This is
how OIDC discovery and service documents work everywhere, and it is what lets the CLI — and the M4
Avalonia app — bootstrap without credentials. The admin surface stays hidden through claims-aware
link emission, not by walling off the root.

The endpoint is mapped through `MapEndpoints(endpointsGroup)` like every other `IEndpoint`, so it
stays inside the rate-limited group; the anonymous root is not a free unauthenticated hit in
production.

A bearer the API refuses (expired, unknown key) is simply not a caller identity on an anonymous
endpoint: the request succeeds and returns the guest link set rather than 401. Token rejection is
still enforced on every protected route.

### 3. Discovery advertises entry points; id-keyed affordances live on the resource that carries the id

Only parameterless routes belong in the service document. `import`
(`POST /api/v1/game-versions/{id}/import`), `delete` and `approve` need an id, and
`LinkGenerator.GetUriByName` cannot resolve a route whose required values are missing — it would
return null and log a warning on every request.

So the ticket's "admin sees register/delete/import" is satisfied across two representations, not one:
`register` is an entry point and ships in the document; `delete` was already on the game-version item
and `import` joins it there. That is also the correct hypermedia shape — a client learns an id by
following `game-versions`, and the affordances for that id arrive with it.

`import` is advertised to an admin on any version that is **not** `Superseded`: re-importing into an
already-processed version is legal (`MarkAsProcessed` refuses only the superseded state), so unlike
`delete` the affordance survives processing.

The alternative — templated links (`{id}` in the href plus a `templated` flag on `LinkDto`) — would
change the link contract in both bounded contexts to buy a client one round trip. Not now.

### 4. The rel vocabulary

Kebab-case, matching the existing style. These names are a frozen public contract:

| Rel | Target | Visible to |
|---|---|---|
| `self` | `GET /` | everyone |
| `translation-file` | `GET /api/v1/translation-files/pl` | everyone |
| `progress` | `GET /api/v1/progress` | everyone |
| `translations` | `GET /api/v1/translations` | everyone |
| `upsert` | `PUT /api/v1/translations` | translator+ |
| `translation-stats` | `GET /api/v1/translations/stats` | translator+ |
| `game-versions` | `GET /api/v1/game-versions` | translator+ |
| `contribution-data-export` | `GET /api/v1/translators/me/data-export` | any authenticated caller |
| `bulk-approve` | `POST /api/v1/translations/approve` | admin |
| `register` | `POST /api/v1/game-versions` | admin |

`translation-file` resolves with `lang = SupportedLanguages.Polish`. Polish is the only language the
platform serves, and the constant already exists; a second language means a second rel or a templated
link, decided then.

## Consequences

### Good

- A client can bootstrap from `GET /` with no credentials and no hardcoded paths. The CLI's
  `api/v1/translation-files/pl` and the frontend's six constants become removable in follow-ups.
- Role rules exist once, at the endpoint. A policy change on an endpoint changes its links
  automatically — the drift that made the alternative unattractive cannot happen.
- The redundant role predicates left in `TranslationAggregateLinkFactory` /
  `GameVersionAggregateLinkFactory` are now a second gate over the same truth, not the only gate.
  They stay because they also carry the **state** rules (`isRemoved`, `Draft`/`NeedsReview`,
  `Unprocessed`, `Superseded`), which authorization knows nothing about.
- Auth API output is provably unchanged: its endpoints already carry explicit `AllowAnonymous` /
  `RequireAuthorization()` metadata that matches the hand-written `isAuthenticated` branch exactly,
  and the full auth integration suite (341 tests, including `DiscoveryHateoasTests` and
  `AccountAggregateHateoasTests`) passes untouched.

### Watch

- **The frontend's TMS discovery cache has no degradation guard yet.** Its auth leg throws
  `AuthenticatedLinksDegradedException` when an authenticated caller does not get
  `export-account-data` back; the TMS leg has no equivalent, because until now a dead bearer produced
  a 401 there. With an anonymous root a dead bearer instead yields the guest link set, which the
  1-day `HybridCache` entry would freeze under the authenticated key. It is **unreachable today** —
  `GetTranslationSystemDiscoveryAsync` has no production caller; only the auth leg is consumed
  (`AccountLoader`). The follow-up that makes frontend pages read TMS discovery must add the mirror
  guard, keyed on `contribution-data-export` (the rel every authenticated caller has).
- Every link now costs one policy evaluation. Trivial at today's link counts; revisit if a
  collection ever emits links per row at a scale where it shows.

### Neutral

- Link factories read `await _linkFactory.CreateAsync(...)` inside `AddIfPresent`. The
  `AddIfPresent` collection idiom and the null-on-unresolvable behavior are unchanged.
- The link factory **fails closed**: an endpoint name that cannot be located advertises nothing.
