# ADR-0067: Who checks what — each of our parts checks only what it owns, by the standard OAuth roles

**Status:** Accepted
**Date:** 2026-10-11
**Decision-makers:** Solo maintainer (owner decision recorded in #1098, 2026-10-11)
**Related:** Frontend (`Infrastructure/Auth/`), AuthSystem.API (the OpenIddict server and the account
API), TranslationSystem.API (`ApiDependencyInjection`, `JwtBearer`); ADR-0008 §3 (startup checks,
amended by this ADR), ADR-0041 (no gateway), ADR-0049 (the revocation window), ADR-0054 (the caller
key), ADR-0062 (the sign-in server's session); tickets #1098, #1058, #1062, #923, #933, #1114, the
chain #974 → #1026/#1028 → #1056/#1057/#1065/#1066/#1067, and #830, #899, #922, #966, #1023, #1054,
#1086, #1108

## Context

### The problem

Review sessions kept filing tickets that asked the website to check the answers of our own sign-in
server. Then the next sessions filed tickets about those checks. Between 2026-09-29 and 2026-10-11,
eight commits changed the website's token renewal code, and eight tickets formed one chain:

- #974 (a renewal accepts an empty access token or no lifetime): checks added (PR #1030).
- #1026 (the first sign-in accepts what the renewal refuses) and #1028 (a renewal stores a broken
  access token): more checks, among them a check that the access token is a three-part JWS (PR #1060,
  PR #1068).
- #1056, #1057, #1065, #1066 and #1067: edge cases of those checks. The size of a token, a lifetime
  the server overstates, a broken shape at the first sign-in, the refresh token left alive after a
  refused answer.

Each of these tickets says almost the same sentence: our own sign-in server always sends a good
answer, so the case needs a broken server or a proxy in front of it. #974 adds: "It is unlikely, but
the answer comes from outside the process". One session even decided both ways. PR #975 did not
file a size limit, because "only our own sign-in server, or Caddy, can send that answer". PR #1068
later filed the same limit as #1065.

The owner stopped the chain on 2026-10-11 (#1098) and closed #1056, #1057, #1059, #1065 and #1066 as
not planned. This ADR writes the rule down, so that the next session does not decide it again.

### Why it happened

No document said which part checks what. Four instructions, each fine alone, added up to a loop:

1. The `code-reviewer` agent's edge-case gate (`.claude/agents/code-reviewer.md`, Phase 6) had two
   states: "impossible by construction", or "must be handled and tested". It called an unhandled
   input "reachable from outside the process" Critical. An answer from our own sign-in server comes
   from another process, so it was always a finding. The gate was written for the patcher (imported
   `||` files, DAT bytes, CLI arguments). There, "outside the process" really means "not trusted".
2. The second review pass in `/ticket` step 7 calls a finding real when "the claim holds when you
   read the code", and says "'Out of scope' never ends a real finding". "The website does not check
   the token's size" is true, so it was real. A finding counted as already decided only when an ADR or
   a ticket said so, and none did.
3. `docs/labels.md` turns each case into a class: "find every case of this class in the flow, fix
   each one, and give each one its own test". So each ticket added several checks, and each check gave
   the next blind review new edge cases.
4. The rulings lived in issue comments, with no line that said how far they reach. PR #1068 read
   the owner's narrow ruling on #974 (a missing lifetime is a failed renewal) as "a damaged answer is
   not trusted at all", and used it to add the shape checks.

The checks also made the website a party to a contract it has no business in. The sign-in server
calls `DisableAccessTokenEncryption()` (`OpenIddictExtensions.cs`), against OpenIddict's default of
encrypting every token. That setting is a contract between the sign-in server and the APIs that read
the token: the translation API holds no decryption key, so it would refuse an encrypted token. The
website's per-request signature check (from the M3-01 lift) already refused an encrypted token too,
and since #1028 its shape check refuses anything that is not a three-part JWS. So a change of the
token format would have to be made in three places instead of two.

### What the standards say

- RFC 9068 §6: "The client MUST NOT inspect the content of the access token." §4: the resource
  server checks the token's `typ`, `iss`, `aud` and `exp`.
- OpenID Connect Core 1.0 §3.1.3.5–3.1.3.8: the client validates the ID token of the token answer.
  `at_hash` is the one access-token check it may do.
- Microsoft identity platform, on access tokens: clients "should treat them as opaque strings. The
  client application shouldn't attempt to validate access tokens. The resource server should
  validate the access token".
- OpenIddict's own client calls its access-token check "typically not used for standard-compliant
  implementations as access tokens are supposed to be opaque to clients".

### The same argument in two neighbouring questions

- **A failed renewal.** #923 asked for a sign-out after a 10-second timeout. PR #1063 revoked the
  refresh token on every failure path, because "the owner ruled no path out". #1062 then showed the
  cost: a deploy or a busy sign-in server signed users out. Two tickets waited twelve days in the
  loop on this question. The owner ruled in #1062 on 2026-10-11.
- **Links and redirects.** On #830 the owner left the OIDC back-channel as it was, because "its
  addresses come from the auth server's own OIDC metadata". The review of #830 filed #899, which put
  the back-channel back in, and it merged. Both were right for different reasons, but nothing wrote
  down the line between them. Decision 4 draws it.

Startup checks of our own settings grew the same way, one review at a time (#853 → #857 → #877 →
#915, #946 → #971, #1025 → #1054 → #1108), with no written bar for where they stop.

## Decision

### 1. The roles are the standard OAuth 2.0 / OpenID Connect roles

| Part | Role | Library |
|---|---|---|
| Website (`Frontend`) | Client (relying party) | ASP.NET Core OpenID Connect handler, plus our renewal hook |
| Sign-in server (`AuthSystem.API`) | Authorization server and OpenID provider | OpenIddict server and ASP.NET Core Identity |
| Translation API (`TranslationSystem.API`) | Resource server | ASP.NET Core `JwtBearer` |
| The sign-in server's account API (`auth/*`, `connect/userinfo`) | Resource server | OpenIddict validation (local server) |

### 2. What each part checks

**The website checks only what OpenID Connect asks of a client.** The OIDC handler does it: the ID
token (signature, issuer, audience, lifetime), `state`, `nonce`, PKCE, `at_hash`, and the `sub` of
the userinfo answer. On top of that, the website reads only what it needs to use an answer: a token
is there and is not blank, and `expires_in` is there and positive, because the renewal schedule
needs it.

One deliberate gap: OIDC Core §3.1.3.7 and §12.2 ask a client to validate the ID token that a
renewal returns, too. The website keeps that token only as the `id_token_hint` of the sign-out, which
the sign-in server checks itself, and reads no claims from it. §3.1.3.7 also lets a client that gets
the token straight from the token endpoint over TLS rely on TLS instead of the signature. So the
website does not validate a renewed ID token. If it ever reads claims from one, it validates the token
with the OIDC handler's own `TokenValidationParameters` and `ProtocolValidator`, the way Microsoft's
Blazor sample does (`CookieOidcRefresher`), and never with a check of its own.

**The access token is opaque to the website.** The website never checks its shape, signature, size,
`exp` claim or characters. Reading `expires_in` from the token answer, above, is not a check of the
token. The website never checks the characters of a refresh token either (RFC 6749 §1.5).

**The translation API checks every access token it receives:** the signature, the issuer, the
audience and the lifetime, and, with #933, the token type `at+jwt` and no clock skew. A token it
refuses gets a 401, and the website's dead-session marker ends that session cleanly on the next
page. This is the path by which a broken or expired token reaches the website: through the part whose
job it is to check it. A revoked session ends at its next renewal instead, when the sign-in server
answers `invalid_grant` (ADR-0049). A resource server's check of a token is never "another part's
job": it is the one place that check belongs.

**The account API checks its tokens the same way** (OpenIddict validation, audience
`lotrokoniecdev-auth-api`). A token for another audience gets 401 `invalid_token` (RFC 6750). That
is what a service token gets today: #1023 changed its answer from the 403 that #966 had chosen, and
kept `UserTokenPolicy` as a second layer that answers 403 if a non-user token ever names this API.
Both layers are the resource server's own checks.

**The sign-in server checks the clients and the grants:** the client's authentication, PKCE, the
authorization code, the refresh token, and the security stamp at every code exchange and every
refresh (ADR-0049, amendment of 2026-09-27). It checks its own settings at startup (decision 6).

### 3. An answer the website cannot use ends the session cleanly, or counts as temporary

The website adds no check for an answer our sign-in server does not send. It sorts every renewal
answer it can get into one of two kinds (owner decisions in #1062 and #923):

- **Temporary:** 429, any 5xx, a network error, a timeout (30 seconds, #923), and a failed fetch of
  the discovery document on the way to the token endpoint. The website keeps the session, revokes
  nothing, and tries again on the next request, while the current access token still works. There is
  no setting for it: the window is the rest of the access token's life, about one minute. This
  window is an accepted trade-off. A session revoked in that minute keeps working until its access
  token runs out, which ADR-0049 already accepts for five minutes.
- **Final:** any other refusal (for example 400 `invalid_grant`), any failure once the access token
  has run out, and a 2xx answer the website cannot use (a body it cannot read, a missing token, no
  lifetime). The website ends the session with the session-expired notice, and revokes the refresh
  token it holds and the new one a refused answer carries (#1027, #1067). Only a broken sign-in
  server sends a 2xx answer that cannot be used, and a retry would get the same broken answer from
  the same server. So it gets the simplest outcome and no further code. #1098 lists "a body it
  cannot read" and "a missing token" with ending the session cleanly, and #1062's temporary list
  names only status codes, a network error and a timeout. A body cut off on the way fails as a
  network error, so it is temporary.

A session that is already ending (a sign-out, or a final failure above) still ends when the
revocation or end-session address cannot be reached: the revoke is skipped and logged
(`RefreshTokenRevoker`), and the session does not come back.

Neither kind ever throws an unhandled exception, and neither shows the error page for a session that
already exists. This one clean path is a rule of its own and keeps its tests: decision 3 removes the
need for a check per broken shape, not the need to end every unusable answer in a defined way (#943,
#972).

### 4. Guards on where our secrets go stay; checks on whether our own server told the truth go

Every part keeps every guard that stops a secret from reaching someone who must not have it: a host
that is not ours, plain HTTP, a page a visitor sees, an e-mail, a log line or a trace. The secrets
are the bearer token, the refresh token, the client secret, the caller key, the session cookie and
any password. The guards include the same-origin check on links and on the revocation endpoint, no
automatic redirects, no shared cookies, the `Secure` flag on the session cookie
(`SameOriginDelegatingHandler`, `CreatePrimaryHandler`; #830, #899, #924, #964, #400), and a startup
check that keeps a password out of an address the sign-in server prints (#1108). The harm there is a
leak, and such a guard is a finding even when only our own server or Caddy could trigger it.
ADR-0041 also expects links to another origin when a service splits off. #922 belongs to this side.

A check that second-guesses what our own server put in an answer does not stay.

### 5. Built-in behaviour first; own protocol code needs a written reason

Behaviour that OpenIddict or ASP.NET Core gives us wins over our own code. A library default is
changed only for a harm a real user can meet in normal use, or one an attacker can cause. ADR-0061
is the second kind: OpenIddict let a GET to the introspection endpoint test client-secret guesses on
a route with no rate limit, so that default went. #1059 is the other side: the OIDC handler's
one-time correlation cookie stays as the library sets it. Own protocol code carries its reason in
its doc comment or in an ADR. #1098 asked for this comparison:

- **The website's renewal stays our own code.** ASP.NET Core has no built-in token renewal
  (dotnet/aspnetcore#8175 is open, in the ".NET 12 Planning" milestone). Our hook in the cookie's
  `OnValidatePrincipal` is the pattern Microsoft documents for Blazor Web Apps. Static SSR also needs
  the renewal to finish before the response starts to stream, and `OnValidatePrincipal` runs before
  that.
  - OpenIddict.Client was not chosen. It replaces the whole OIDC handler, still leaves the renewal to
    us, and keeps state tokens in a database the website does not have. The other option,
    `DisableTokenStorage()`, is one its docs advise against.
  - Duende.AccessTokenManagement (Apache-2.0) was not chosen. It renews when a page asks for a token.
    On a streaming page that can be after the response has started, when the cookie can no longer
    change.
  - What stays our own code moves toward Microsoft's sample. The renewal and sign-out addresses are
    read from the discovery document instead of being built from `connect/token` and `connect/logout`
    (#1114).
- **The translation API stays on `JwtBearer`,** with #933's two settings (`ClockSkew = 0`,
  `ValidTypes = at+jwt`). It is ASP.NET Core's own handler, the change is configuration only, and it
  keeps the TheKittySaver lift (`docs/kittysaver-lift-map.md`). Reopen this when a third rule has to
  be set by hand, or when encrypted access tokens are wanted. Then OpenIddict validation with
  `AddEncryptionKey` is the built-in path.

### 6. Our own settings are checked once, by the part that owns them

Our configuration (`appsettings*.json` and each box's `.env`) is written and reviewed by the owner.
It is trusted input. The part that owns a setting checks it at startup. Another part never makes up
for it and never keeps a copy of it. `GdprSettingsValidator.TmsClockSkew` is such a copy of the
translation API's clock skew. #933 sets it to zero with the change on the API, as the owner ruled
there. With no skew left on the API there is nothing to copy, so the constant can go in that change.
The bar for a startup check is in ADR-0008 §3, amended by this ADR.

### 7. What stays a finding

"Another part's job" (§8) covers one narrow kind of finding: a new check of what one of our own
parts sends, for an answer that part never sends by design. "Breaking its contract" in this ADR means
exactly that: sending an answer the contract says it never sends, such as a token of the wrong shape
or a lifetime that is not true. These five always stay findings, whoever could trigger them, and
every instruction file repeats them:

1. **Our own part being down, slow or failing.** A 5xx, a timeout, a closed broker channel, a failed
   database save: every part handles these, and a case nobody handles is a valid finding (#908,
   #937, #1088, #923, #1062).
2. **The one clean path for an answer the code cannot use** (decision 3). It ends in a defined
   outcome, never an unhandled exception, and it keeps its tests (#943, #972).
3. **A guard on where a secret goes** (decision 4).
4. **A resource server's check of a token it receives** (decision 2). Adding one to the translation
   API or the account API, such as #933's `at+jwt`, is never "another part's job".
5. **Input from outside our trust boundary.** It keeps every check it has, and gets one for every new
   case a review finds: browser requests and form fields, anything a user types, imported files
   (`exported.txt`, the Companion zip), DAT bytes, CLI arguments, the CLI's settings on a player's
   machine, the CLI's download from the translation API over the public internet to an address the
   player sets (#391, #394, #611), third-party services (Pwned Passwords, Brevo, the LOTRO forum),
   and ticket text in this public repo. Where the data came from decides, not the last part it
   passed through: text a translator typed is still outside input when our own API hands it to the
   website.

Two more points:

- **Limiting the damage after a break-in** is a separate question. Least privilege between our own
  parts stays a valid finding: #1086 (the translation API should not hold the broker's admin account)
  is not closed by this ADR. It is solved with credentials and permissions, never by checking data
  again where it arrives.
- **A real path that makes our own part break its contract** is a defect of that part. If a review
  shows how our sign-in server could send a broken token (a setting, a code path, a library upgrade),
  the ticket goes to the sign-in server, with that path in it. Without such a path, the case is not
  filed.

### 8. Not a finding

A review finding of one of these kinds is sorted as "already decided", with a pointer to this
section. It is not filed, not fixed, and not added to another ticket:

- The website checking an access token (its shape, signature, size, `exp` claim or characters), or
  the characters of a refresh token.
- A new check, in the website, for each kind of token answer our sign-in server does not send (a
  broken shape, an oversized token, a lifetime the server overstates, an unknown charset). Decision
  3's one clean path covers all of them, and that path keeps its tests.
- The first sign-in and the renewal "disagreeing" about such answers (#1056, #1066).
- A protocol answer where the built-in OpenIddict or ASP.NET Core behaviour is used, the standard
  leaves the form open, and the client already gets what it needs (#917: a 429 or a 500 from the
  token endpoint is `ProblemDetails`, and the status code tells a temporary failure from a final
  one). A page or a message a person reads is not covered (#912).
- A library default whose harm needs abnormal use by the user themselves and gives an attacker
  nothing (#1059: a leftover one-time cookie that expires within the hour).
- The renewal keeping a session for the rest of the access token's life while the sign-in server
  fails for a moment (decision 3).
- A startup check below the bar of ADR-0008 §3.

## Consequences

### Positive

- One question now settles the next finding of this kind: who owns this check? A session answers it
  from this file, without waiting for the owner.
- The website's token code gets smaller. #1058 removes the per-request signature check and the shape
  checks, and the website's pages keep working while the sign-in server is down after a website
  restart.
- The token format is a contract between the sign-in server and the APIs only. Encrypting access
  tokens one day means giving the APIs the key, not changing the website as well.
- The translation API is the one place that judges an access token, which is where RFC 9068 puts it.

### Negative / Accepted Trade-offs

- A broken token from our own sign-in server is caught the first time the website calls an endpoint
  that needs a sign-in: the 401 marks the session dead, and the next page ends it. Until then, the
  public pages, which also accept anonymous callers, show what an anonymous visitor sees. The window is
  at most the access token's lifetime (five minutes). Accepted: it needs our own server to be broken,
  and a check on every page for that case cost more than it saved.
- A token that an HTTP header cannot carry gets no 401 and no dead-session mark. With a line break
  in it, .NET refuses to build the header (`FormatException`), so each page that calls an API shows
  the error page until the token runs out (at most five minutes). With a non-ASCII character, each
  call fails as a network error. Accepted for the same reason: only a broken sign-in server sends
  such a token, and decision 3's "never the error page" is about the renewal answer, not about this.
- After a hard swap of the signing key, the same happens to each live session. A planned rotation with
  `PreviousRsaPrivateKeyXml` has no effect on users. QA #632's key-rotation step changes with #1058.
- For the rest of an access token's life (about a minute), a session survives a temporary failure of
  the sign-in server, even when it was revoked in that minute.
- Nobody checks the ID token a renewal returns. It is only the sign-out hint. A broken one means the
  sign-in server does not find the session at sign-out, and its own session there ends at its own
  time.

## Alternatives Considered

### A. The website as defense in depth: check everything the sign-in server sends

Rejected. It was tried from #974 to #1068. Each check had edge cases of its own, each review filed
them, and the checks tied the website to a sign-in server setting. RFC 9068 forbids the client to
inspect the access token, and the translation API already checks every token it receives.

### B. Decide each case in the ticket that raises it

Rejected. This is what happened. A ruling in an issue comment says nothing about how far it reaches,
so the next session stretched it (PR #1068 and the #974 ruling) or missed it (#1023 was filed nine
hours after the owner ruled on #966, and reversed that ruling without naming it).

### C. Move the website to OpenIddict.Client or to Duende.AccessTokenManagement

Rejected for now (decision 5). Neither one removes our renewal hook, and OpenIddict.Client needs a
database the website does not have.

### D. Move the translation API to OpenIddict validation now

Rejected for now (decision 5). `JwtBearer` with #933's settings checks the same rules and keeps the
TheKittySaver lift. The reopen triggers are in decision 5.

### E. Introspection on the translation API, so it also sees a revoked token at once

Rejected by ADR-0049: it puts the sign-in server in the path of every API request. Nothing here
changes that.

## Implementation Notes

- **Removed by #1058** (the checks that break decision 2), all in `CookieTokenRefresher`: the
  per-request signature check `IsAccessTokenCryptographicallyValidAsync`, its call in `ValidateAsync`
  and the branch that skips it right after a renewal; and, in `TryRefreshIfNearExpiryAsync`, the
  `IsCompactJws` check on the access token, the printable-ASCII check on the refresh token, and the
  `IsCompactJws` check on the renewed ID token. The owner's comment on #1058 names the first two; the
  last two are the same kind under this ADR. The comments that describe the signature check as the
  main path go with them (`TranslationContentNegotiationAndAuthDelegatingHandler`,
  `AuthContentNegotiationAndAuthDelegatingHandler`, `CookieTokenRefresher`), so does the sentence in
  `TokenRules` that reasons from "our own sign-in server always sends", and so do the matching parts
  of `docs/tms-handbook.md` and `docs/auth-tutorial.md` §8.4.
- **Kept:** the OIDC handler's checks, the blank-token and lifetime reads (`TokenRules`), the
  dead-session marker, the JSON read from the bytes and `UnknownCharsetDelegatingHandler` (they read
  JSON the way RFC 8259 says, nothing more), `SameOriginDelegatingHandler` and `CreatePrimaryHandler`
  (decision 4), and `RefreshTokenRevoker`.
- **Changed by #1062:** `TokenEndpointClient.RefreshAsync` tells a temporary failure from a final one
  (decision 3).
- **Changed by #933:** `ClockSkew = 0` and `ValidTypes` in
  `TranslationSystem.API/ApiDependencyInjection.cs`, and `GdprSettingsValidator.TmsClockSkew` removed
  (decision 6). The comment there that said `JwtBearer` "avoids OpenIddict's own scope permission
  checks" is gone.
- **#1114:** read the renewal and sign-out addresses from the discovery document (decision 5). A
  failed discovery fetch on that path is a temporary failure (decision 3); Microsoft's sample calls
  `GetConfigurationAsync`, which throws when nothing is cached yet (the #1058 failure).
- **ADR-0008 §3:** amended with the bar for a startup check (decision 6).
- **Review instructions:** `.claude/agents/code-reviewer.md` (Phase 1 reads this ADR, Phase 6 has a
  third state, "another part's job", and the gate counts inputs from outside our trust boundary
  instead of inputs from outside the process), `.claude/commands/ticket.md` steps 2 and 7,
  `.claude/commands/work-ticket.md`, `.claude/commands/preflight.md`, `docs/labels.md` → "One bug
  class per ticket", and a house rule in `CLAUDE.md`.

## References

- Tickets: #1098 (this decision), #1058, #1062, #923, #933, #1114; the chain #974, #1026, #1028,
  #1056, #1057, #1065, #1066, #1067; #830, #899, #922, #924, #964 (secrets); #966, #1023 (the account
  API); #917, #1059 (built-in first); #1086 (not covered).
- ADRs: ADR-0008 §3 (amended), ADR-0041, ADR-0049, ADR-0054, ADR-0062.
- RFC 6749 §1.5 and §5.1; RFC 6750 §3.1; RFC 9068 §4 and §6 (https://www.rfc-editor.org/rfc/rfc9068);
  OpenID Connect Core 1.0 §3.1.3.5–3.1.3.8 and §12.2 (https://openid.net/specs/openid-connect-core-1_0.html).
- Microsoft: "Access tokens in the Microsoft identity platform"
  (https://learn.microsoft.com/en-us/entra/identity-platform/access-tokens); "Secure an ASP.NET Core
  Blazor Web App with OpenID Connect", the custom cookie refresher
  (https://learn.microsoft.com/en-us/aspnet/core/blazor/security/blazor-web-app-with-oidc);
  dotnet/aspnetcore#8175.
- OpenIddict: token formats (https://documentation.openiddict.com/configuration/token-formats);
  integrating with a remote server instance
  (https://documentation.openiddict.com/guides/getting-started/integrating-with-a-remote-server-instance).
- Duende.AccessTokenManagement: web apps and Blazor Server
  (https://docs.duendesoftware.com/accesstokenmanagement/web-apps/).
