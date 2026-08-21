# ADR-0049: The revocation window is the access-token lifetime — five minutes, not introspection

**Status:** Accepted
**Date:** 2026-08-21
**Decision-makers:** Solo maintainer
**Related:** #686 (SEC-08, the defect), #701 (QA-FE-24 S03 TC06/TC07, where a tester hit it), ADR-0048 (the e-mail-change undo this protects), ADR-0041 (no API gateway), ADR-0031 (deletion grace period), `OpenIddictSettings`, `IUserSessionRevoker`, `SecurityStampCookieValidator`, `CookieTokenRefresher`, `DeadSessionRegistry`

## Context

Four flows end every session a user has: `RevertEmailChange`, `ConfirmEmailChange`, `ResetPassword`
and `ChangePassword`. They all do the same two things — `IUserSessionRevoker.RevokeAllAsync` revokes
the OpenIddict tokens and authorizations, and the rotated security stamp makes
`SecurityStampCookieValidator` drop the auth-server cookie on its very next request.

Both are correct, and neither reaches an access token that has already been handed out.

The TMS validates access tokens on its own. `ApiDependencyInjection` registers a plain
`AddJwtBearer()` against the authority's JWKS, so the only things checked are the signature, the
issuer, the audience and `exp`. A revoked row in the auth database is invisible to it. And the
frontend never notices either: `CookieTokenRefresher` only calls the token endpoint 60 seconds
before the access token expires, so a page refresh checks nothing at all.

So the revocation is real but **latent**. Nothing presents the revoked credential until the access
token runs out. With the old 60-minute lifetime, someone who had taken an account over kept full API
access for up to an hour *after* the owner clicked "to nie ja" — which is the one moment the undo
exists for. A QA tester found it from the other side: they changed their address, were told every
session was closed, and watched their other browser stay logged in.

**The access-token lifetime is the fuse length on every revocation in this system.** That is the
whole finding, and it reframes the setting: `AccessTokenLifetimeMinutes` is not a performance knob,
it is a security parameter.

One consequence is worth stating, because it changes what has to be built. Once the TMS refuses a
token, the frontend needs no new code: the delegating handler already turns a `401` into a
`DeadSessionRegistry` marker, and the next `OnValidatePrincipal` signs the user out cleanly with the
session-expiry notice. The same happens when a refresh finally fails. The frontend half of this
problem is already solved and waiting for a signal.

## Decision

**Cut `AccessTokenLifetimeMinutes` from 60 to 5, accept the residual five-minute window, and do not
build introspection.**

A five-minute JWT reaches the same end state as introspection. The attacker loses the API, and their
failed refresh drives the existing dead-session path so their browser is signed out too. The only
difference is *when*: about five minutes instead of about thirty seconds.

The cost of the shorter lifetime is small and was measured rather than assumed. Reference refresh
tokens write one row per refresh, so an active user produces twelve rows an hour and
`OpenIddictPruneService` keeps fourteen days of them. A translator working two hours a day leaves
roughly 340 rows inside the retention window. At fifty translators that is about 17 000 rows. This
is not a load a small Postgres notices.

### What the tests pin

Two facts, both against real artefacts rather than against reasoning:

1. A token minted by the real token endpoint reports a lifetime of five minutes
   (`TokenEndpointTests.PasswordGrant_ShouldMintAnAccessTokenThatExpiresInFiveMinutes`). The test
   host deliberately no longer pins its own lifetime, so it runs whatever ships in
   `appsettings.json` — a test host cannot keep proving an old window after production moved.
2. A session minted *after* an account was taken over cannot renew itself once the owner clicks the
   undo (`EmailChangePageTests.RevertPage_Post_ShouldLeaveTheTakeoverSessionUnableToRenewItself`).

Together they bound the takeover: the ability to mint a new token dies immediately, and the token
already held dies within five minutes.

### The pages stop over-promising

Three screens told the user every session was closed, full stop. That was never true for an app
session still inside its token's lifetime. `ConfirmEmailChange`, `ResetPassword` and the frontend's
`DeleteAccount` now say the sessions were invalidated and that any other device is signed out within
a few minutes. A security promise that is wrong for five minutes is worse than a smaller promise
that is always right.

## Rejected alternatives

**Introspection on the TMS.** Swap `AddJwtBearer()` for OpenIddict validation with
`UseIntrospection()`. It closes the window down to whatever the response cache allows, and it is the
textbook answer. Rejected because it puts auth-api on the critical path of *every* TMS request to
buy four and a half minutes. That is a real availability coupling — auth down means TMS answers 401
— and it runs against ADR-0041's rule that nothing sits in the request path. At a few dozen
translators the security gain does not pay for the coupling.

**`EnableTokenEntryValidation` over a shared token table.** Have the TMS read the OpenIddict token
entries directly, with no HTTP hop. Rejected on a fact rather than on taste: `lotro_auth` and
`lotro_translation` are **separate databases**, so the TMS would need a second connection into the
auth database and a hard dependency on the OpenIddict schema. That is the bounded-context isolation
`Architecture.Tests.Unit` exists to protect.

**A monotonic stamp claim checked by the TMS.** Put the security stamp in the access token and have
the TMS reject any token carrying a stamp older than the newest it has seen. Rejected because the
TMS only learns of a new stamp when a new token arrives, so the protection depends on the victim
logging in promptly — security that waits for the person who was just attacked. A `Guid` stamp is
also not ordered, so it would need a version counter, which is new state in the wrong context.

**Redis plus a shared revocation cache.** Rejected as new infrastructure for one feature. There is
no `IDistributedCache` anywhere in the stack today; both `HybridCache` registrations are in-memory.

**Leaving the lifetime at 60 and documenting the window.** Rejected: the undo's entire purpose is to
end a takeover now, and an hour is not now.

## Consequences

- **The window is five minutes and it is real.** Someone holding a live access token keeps API
  access for up to five minutes after the owner revokes everything. This is accepted, not fixed.
  It is written on the page the user reads, in this ADR, in ADR-0048's undo section and in the
  runbook.
- **The setting is now load-bearing.** Raising `AccessTokenLifetimeMinutes` lengthens the window on
  every credential change, deletion and undo in the system. Nobody should raise it without reading
  this ADR, which is why the property carries the reason in its doc comment.
- **The test host follows production.** Token lifetimes were removed from
  `AuthSystemApiFactory`'s in-memory configuration, so integration tests exercise the shipped value.
- **More refresh traffic and more token rows.** Twelve refreshes an hour per active user instead of
  one. `OpenIddictPruneService` already handles the rows; if the table ever becomes a problem, the
  prune interval is the knob, not the lifetime.
- **The frontend needed no change.** The `DeadSessionRegistry` path already converts a failed
  refresh, or any 401, into a clean sign-out with the expiry notice.

**Reopen this decision when:** five minutes is judged too long — most likely once there are real
users and a real takeover — or a second consumer of user access tokens appears outside the frontend,
which would make one shared introspection point cheaper than it is today.
