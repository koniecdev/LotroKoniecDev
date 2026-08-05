# ADR-0041: No API gateway — Caddy owns transport, hypermedia discovery owns the contract surface

**Status:** Accepted
**Date:** 2026-08-05
**Decision-makers:** Solo maintainer
**Related:** #609 (the ticket), #608 / ADR-0040 (the discovery document this decision leans on), #610 (frontend follow-up), #611 (CLI follow-up), ADR-0034 (Hetzner VPS + Caddy ingress), ADR-0006 (frontend as an SSR BFF), `.docker/hetzner/Caddyfile`, `LinkFactory`, `DiscoveryLinkFactory`, `HttpClientsDependencyInjectionExtensions`

## Context

The platform exposes three origins: the frontend, the auth API and the TMS API. The frontend talks
to both APIs; the CLI talks to the TMS API directly; the M4 Avalonia app will do the same. The
question was whether a dedicated API gateway (YARP) should sit in front of them — and it was asked
with a future service split out of the TMS in mind, where "we split a service, so we need a gateway"
is the default reflex.

It does not apply here, because every job a gateway would take is already owned by something in the
repo, and one of them is owned *better*:

- **Transport, TLS, host routing.** Caddy is already the ingress (`.docker/hetzner/Caddyfile`,
  ADR-0034): one vhost per app, ACME certificates, `X-Forwarded-*` for `UseForwardedHeaders`,
  `X-Robots-Tag`. It also already fronts a second, segregated stack (TheKittySaver) as the only
  shared trust boundary between the two — so "route several backends behind one TLS terminator" is
  not a hypothetical capability here, it is in production.
- **Response aggregation for the browser.** The frontend is Blazor **Static SSR**, so the browser
  only ever talks to the frontend origin — the sole exception is the OIDC front-channel redirect,
  which must stay a browser redirect for the token `iss` to match. The frontend already *is* a BFF:
  typed clients with Polly retry, circuit breaker and per-request timeouts
  (`Frontend/Infrastructure/HttpClients/HttpClientsDependencyInjectionExtensions.cs`). A gateway
  would aggregate for a browser that never calls the APIs.
- **Rate limiting.** Both APIs run ASP.NET's rate limiter with **per-endpoint policies**
  (`auth-endpoint-limit`, `forgot-password-limit`, `resend-confirmation-limit`, and the group limit
  the TMS service document sits inside). A gateway limit sees a URL and an IP; the app-side limiter
  sees the endpoint, the policy and the authenticated caller. Moving it outward would be a
  downgrade, not a consolidation.
- **A stable URL surface while backends move.** This is YARP's one genuine selling point in a
  service split — and it is exactly what a hypermedia discovery document delivers, at the
  representation layer, with no extra network hop and no routing table to keep in sync with the
  code.

Token validation is deliberately **not** a gateway job. Every API validates the JWT itself (zero
trust); the TMS additionally replays each target endpoint's own authorization policy before
advertising a link (ADR-0040). A gateway as the single guard would concentrate that into one
bypassable choke point.

The discovery mechanism was only half-built when the question was first raised — the TMS root
advertised one `self` link behind a bearer token, which is precisely what made a gateway look
necessary. #608 / ADR-0040 closed that gap: the root is anonymous, its contents are claims-aware,
and it advertises ten rels. The clients still hardcode paths (the CLI's
`api/v1/translation-files/pl`, the frontend's six constants) — that is #611 and #610, not a missing
capability.

## Decision

### 1. No API gateway. Three layers, three owners

| Layer | Owner | Job |
|---|---|---|
| Transport | Caddy | TLS/ACME, host routing, forwarded headers |
| Semantics | the discovery document of each API | what exists and where it lives |
| Aggregation | the frontend (BFF) | composing calls for the browser |

Nothing is added to the request path. A client resolves an origin through DNS + Caddy, then
resolves *everything else* by following links from that origin's root.

### 2. Clients resolve endpoints by rel name, never by path

The frontend, the CLI and the future Avalonia app take **one root URL per service** as
configuration and obtain every other URL from the discovery document by link relation name. That
collapses the frozen public contract from "every path, forever" down to "one root URL plus a set of
rel names" — which is what makes a service split cheap without a proxy in the middle: paths may be
reorganised freely as long as the rel keeps resolving.

This is not aspirational. The frontend already holds exactly two configured base URLs
(`TranslationSystemSettings.BaseUrl`, `AuthSystemSettings.BaseUrl`) for two independently deployed
services, with no gateway between them, and `DiscoveryCache` already consumes the auth service
document. The split-service case is the case the repo is already living in.

### 3. A split service gets its own discovery root; the parent advertises one link to it

`LinkFactory` resolves URIs through ASP.NET's `LinkGenerator`
(`src/Utilities/LotroKoniecDev.Hateoas/LinkFactories/LinkFactory.cs`, `GetUriByName` in
`CreateAsync`), which only knows routes registered in the same application. So the moment a service
moves to its own host, its links cannot be generated by the service it left.

The approach, decided here so the split does not have to relitigate it:

- The departing service **hosts its own discovery root** and owns the rels beneath it, exactly as
  the auth API does today.
- The service it left advertises **one** link to that root — an absolute URI read from
  configuration, in the same shape as the frontend's existing per-service `BaseUrl` settings. One
  configured value per foreign service, never a per-route map.
- `ILinkFactory` keeps its `LinkGenerator` path for same-app endpoints. A cross-service link is a
  configured absolute URI, so it needs a separate, explicitly-named affordance rather than a
  silent fallback inside `CreateAsync` — a typo in an endpoint name must keep failing closed
  (returning no link) rather than resolving to some configured host.

Rejected for that case: **a per-route URI map in configuration.** It is a routing table living
outside the code that owns the routes and drifting from it — the precise defect that disqualified
YARP. Rejected: **templated cross-service links**, which change `LinkDto` in both bounded contexts
to save a round trip nobody is paying for yet.

**Consequence, stated up front:** ADR-0040's authorization-aware emission does **not** cross a host
boundary. `LinkFactory` can only replay a policy for an endpoint in its own application, so a link
to a foreign root is emitted unconditionally. That is acceptable and self-correcting — the foreign
root is itself claims-aware, so a caller who may see nothing there receives a document with nothing
in it. It must not be papered over by re-stating the foreign service's role rules locally; that is
the drift ADR-0040 exists to prevent.

Implementing any of §3 is out of scope until a split actually happens.

## Consequences

### Good

- No process, no hop, no routing configuration to operate or keep in lockstep with the code.
- The public contract shrinks to one root URL plus a rel vocabulary. Paths become an internal
  detail of the service that owns them.
- Authorization stays where it is enforced. There is no single guard whose bypass reaches
  everything.
- Rate limiting stays per-endpoint and identity-aware.

### Accepted trade-offs

- **Rel names are now the frozen public contract, and must be chosen as carefully as paths once
  were.** Renaming a rel breaks every client that resolves by it — exactly as renaming a path used
  to. The rel table in ADR-0040 §4 is that contract; additions are cheap, renames are not.
- Clients pay one extra request to fetch the discovery document. Both existing consumers cache it
  (`DiscoveryCache`), and the CLI's sync already makes a conditional request anyway.
- Cross-origin concerns (CORS for the auth front-channel, per-origin certificates) are handled per
  vhost in Caddy instead of centrally. That is the topology already in production.

### Neutral

- The decision is reversible in one direction only, and cheaply: adding a gateway later in front of
  origins whose clients already resolve by rel name is strictly easier than removing one whose
  routing table clients have hardcoded.

## Alternatives Considered

### A. YARP

The mainstream .NET answer, and the one the question was actually about. Its jobs here split into
two piles: transport (TLS, host routing, forwarded headers) — already Caddy's, and duplicating it
means two things to configure and one to forget; and semantics (a stable URL surface across a
backend reshuffle) — already the discovery document's, delivered at the representation layer where
the code that owns the route also owns the link. What is left is a process to deploy, a hop on
every request, and a routing configuration that can silently drift out of sync with the routes it
proxies. Rejected.

### B. Ocelot

Effectively unmaintained. Rejected on that alone; the capability argument against YARP applies to
it equally.

### C. Kong / Traefik

Another runtime to operate, monitor and patch on a two-box, ~4 GB-per-box Hetzner deployment
(ADR-0034), for capabilities already covered by a reverse proxy that is running and proven. Traefik
would additionally mean replacing Caddy rather than adding to it — a migration with no benefit at
this scale. Rejected.

### D. Keep hardcoded paths in every client and skip discovery too

The status quo before #608, and the reason a gateway looked necessary: with no discovery, the only
way to keep client paths stable across a backend change *is* a proxy. Rejected — it trades a
representation-layer solution for an infrastructure one and freezes every path forever.

## Reopening triggers

This decision is scoped to the platform as it stands. Revisit it when any of these becomes true:

- **A public third-party API** needing per-client API keys, quotas, or a rate limit shared across
  several backends. Per-endpoint app-side limiting does not span services, and issuing keys to
  third parties is a gateway-shaped problem.
- **More than roughly four publicly exposed services**, where one vhost per app in the Caddyfile
  stops being readable at a glance.
- **A need to version the public URL surface independently of how services are split** — e.g.
  keeping `/api/v1/...` alive on a schedule that no longer matches any single service's lifecycle.

Absent one of these, "we split a service" on its own is explicitly **not** a trigger — §2 and §3
are the answer to that case.

## Implementation Notes

- This ADR records a decision; it changes no production code.
- Numbered **0041**, not 0040 as the ticket title says: #608 landed as
  `0040-authorization-aware-hateoas-links-and-an-anonymous-tms-root.md` first.
- The client-side half of §2 is tracked as #610 (frontend: drop the six hardcoded paths) and #611
  (CLI: drop `TranslationFileRoute`). Neither is blocked by this ADR.

## References

- ADR-0040 — the discovery document, its rel vocabulary (§4) and the authorization-aware emission
  this decision relies on
- ADR-0034 — the Hetzner topology, Caddy as ingress, the two-stack trust boundary
- ADR-0006 — the frontend's role, and why the browser talks to one origin
- `.docker/hetzner/Caddyfile`; `src/Utilities/LotroKoniecDev.Hateoas/LinkFactories/LinkFactory.cs`;
  `src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Hateoas/DiscoveryFactories/DiscoveryLinkFactory.cs`;
  `src/Frontend/LotroKoniecDev.Frontend/Infrastructure/HttpClients/HttpClientsDependencyInjectionExtensions.cs`
