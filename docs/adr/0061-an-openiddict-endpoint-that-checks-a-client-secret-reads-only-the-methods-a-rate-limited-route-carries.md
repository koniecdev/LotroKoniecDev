# ADR-0061: An OpenIddict Endpoint That Checks a Client Secret Reads Only the Methods a Rate-Limited Route Carries

**Status:** Accepted
**Date:** 2026-09-28
**Decision-makers:** Solo maintainer (ticket #900)
**Related:** AuthSystem.API (`Extensions/OpenIddictExtensions.cs`, `Features/Auth/MiddlewareServedEndpoints.cs`,
`Program.cs` rate limiting); ADR-0049 (the TMS API validates access tokens itself, not through
introspection), ADR-0054 (the refused-call warning, amended by #854); tickets #347, #349, #854, #900

## Context

The brute-force limiter `auth-endpoint-limit` (10 requests a minute per client address) runs after
routing and before authentication (#347). It reads its policy from the metadata of the endpoint that
routing matched. OpenIddict serves introspection and revocation itself, during authentication, with no
passthrough to a handler of ours. So `MiddlewareServedEndpoints` maps routes for `connect/introspect`
and `connect/revoke` that only carry that policy (#349). Both map POST only.

A request with a method that no route maps lands on routing's 405 endpoint. That endpoint has no
metadata, so it gets no limit. #349 said this was harmless, because OpenIddict refuses a non-POST
request before it checks the client. That is true for token and revocation, and false for
introspection. In OpenIddict.Server.AspNetCore 7.7.1:

- Token, revocation, device authorization and pushed authorization are read by
  `ExtractPostRequest`, which refuses anything but POST with `400 invalid_request`.
- Introspection is read by `ExtractGetOrPostRequest`, which also reads a GET query string.
- The core `ValidateClientSecret` checks the secret for token, introspection, revocation, device
  authorization and pushed authorization. It skips authorization, end session, userinfo and end-user
  verification. Those three routed ones (`connect/authorize`, `connect/logout`, `connect/userinfo`)
  map both GET and POST anyway.

So a GET to `connect/introspect` with `client_id` and `client_secret` in the query was checked: the
right secret got 200 and a wrong one got 401, with no limit at all (#900). The only client with a
secret is our own confidential `lotrokoniecdev-api`, and nothing calls introspection today: the TMS
API validates tokens itself (ADR-0049).

## Decision

### 1. Every endpoint that checks a client secret reads only the methods a rate-limited route carries

A method with no route must be refused before the client is looked up. Then the right secret and a
wrong one get the same answer, and a caller cannot test guesses through that method at all, with or
without a limit.

### 2. Introspection reads POST only

`OpenIddictExtensions` removes OpenIddict's `ExtractGetOrPostRequest<ExtractIntrospectionRequestContext>`
handler and adds `ExtractPostRequest<ExtractIntrospectionRequestContext>`, the reader token and
revocation already use. A GET, or any other method but POST, gets `400 invalid_request` before the
client is looked up. RFC 7662 §2.1 defines introspection over POST, so no correct client loses
anything.

### 3. The test guards the rule, not a list of routes

`ConnectRateLimitingTests` reads the URIs of every endpoint type that checks a client secret from
`OpenIddictServerOptions`. For each one without a GET route, a GET carrying the right secret must be
refused with `400 invalid_request`. A new endpoint, or an OpenIddict upgrade that widens a reader,
fails there instead of shipping a method with no brake.

## Consequences

### Positive

- The gap closes with no new route and no new limiter.
- A client secret is never accepted from a URL. Our request log hides `client_secret` (the
  `SensitiveDataRedactor` list), but no rule should depend on every hop that logs a URL doing the
  same.
- Introspection behaves like token and revocation, and the #349 comment in
  `MiddlewareServedEndpoints` is true again.

### Negative / Accepted Trade-offs

- We name two OpenIddict handler types that are public but marked for advanced use. A rename breaks
  the build, which is loud. A change in what they do is caught by the test in decision 3.
- A flood of GET requests still meets no limiter. Each one gets a 400 with no database lookup, the
  same as a GET to `connect/token` always did.
- A client that sends introspection over GET breaks. None exists, and RFC 7662 never allowed one.

## Alternatives Considered

### A. Map the metadata route for GET as well

`MapMethods(pattern, ["GET", "POST"], …)` gives both methods one brake, and a refused GET would be
warned like a refused POST (ADR-0054). Rejected. It keeps accepting a client secret from a query
string, it goes beyond RFC 7662, and the rule would still depend on someone mapping every method
OpenIddict happens to read.

### B. Refuse a non-POST introspection request in our own OpenIddict event handler

Same effect as the chosen swap. Rejected: more code for what OpenIddict's own POST-only reader
already does.

### C. Key the limiter on the OpenIddict endpoint type or the `/connect/*` path

It would limit every method, routed or not. Rejected for now. It replaces the metadata-based limiter
of #347 and #349, which every other per-endpoint policy also uses, with a second mechanism for one
path prefix. Revisit if another gap of this kind appears.

## Implementation Notes

- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Extensions/OpenIddictExtensions.cs` — the handler
  swap, inside `AddServer`, after `UseAspNetCore()`. `RemoveEventHandler` works in a `PostConfigure`,
  so the order of the two calls does not matter.
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Features/Auth/MiddlewareServedEndpoints.cs` — its
  summary points here.
- `tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/Tests/RateLimiting/ConnectRateLimitingTests.cs`
  — the guard of decision 3.
- `tests/LotroKoniecDev.AuthSystem.API.Tests.Unit/Middleware/AuthorizationLoggingMiddlewareTests.cs`
  — a 401 with no real endpoint is not warned. The integration suite proved that with a GET to
  `connect/introspect`, which is now a 400.
- TheKittySaver has no OpenIddict server any more (its ADR-0007), so there is nothing to mirror.

## References

- Tickets #347 (the limiter runs before authentication), #349 (metadata-only routes), #854 (the
  refused-call warning), #900 (this gap).
- ADR-0049, ADR-0054 (the #854 amendment).
- RFC 7662 §2.1, Introspection Request: https://www.rfc-editor.org/rfc/rfc7662#section-2.1
- OpenIddict.Server.AspNetCore 7.7.1, `OpenIddictServerAspNetCoreHandlers.Introspection.DefaultHandlers`.
