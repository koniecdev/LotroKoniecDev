# ADR-0062: The Sign-in Server's Cookie Carries Only a Session Key, and the Session Lives in the Auth Database

**Status:** Accepted
**Date:** 2026-10-05
**Decision-makers:** Solo maintainer (ticket #1013)
**Related:** AuthSystem.API (`Program.cs` cookie setup, `Services/Sessions/SignInSessionCookieHandler.cs`,
`Services/Sessions/SignInSessionTicketStore.cs`,
`Services/Maintenance/SignInSessionPruneService.cs`, `Services/Gdpr/AccountErasureService.cs`);
AuthSystem.Persistence (`Sessions/SignInSession.cs`, the `SignInSessions` table); ADR-0023 (migrations
are expand-only), ADR-0031 (two-phase account deletion); tickets #282 (SEC-03), #931, #963, #970, #1013

## Context

When you sign in, the sign-in server (auth-api) gives your browser its own cookie,
`LotroKoniecDev.Auth`, so it does not ask for the password again. With "Zapamiętaj mnie" that cookie
lasts 30 days.

Until now the cookie carried everything inside itself: the user id, name, e-mail, roles, the security
stamp and the expiry, encrypted with the server's Data Protection keyring. The server kept no list of
these cookies. That had three results:

- Signing out could only delete the cookie in the browser that signed out. The server had nothing of
  its own to delete, so a copy of the cookie (malware, a shared computer) kept working until it expired.
- The cookie slides: a copy that someone keeps using renews itself, so it does not even run out after
  30 days.
- Only a change of the security stamp killed every copy: a password change or reset, an e-mail change,
  scheduling or cancelling a deletion. Each of those also ends the sessions on every other device, so it
  cannot stand behind a normal "Wyloguj". Signing out ends this device only (owner decision on #931).

## Decision

### 1. The cookie carries a session key, and the ticket lives in the auth database

The `IdentityConstants.ApplicationScheme` cookie gets a session store: ASP.NET Core's
`CookieAuthenticationOptions.SessionStore`, implemented by `SignInSessionTicketStore`. On sign-in the
cookie handler hands the whole ticket to the store, and the cookie then carries only the key the store
returns. Every request that reads the cookie looks the ticket up by that key.

Signing out calls the store's `RemoveAsync`, which deletes the row. Every copy of the cookie names the
same row, so they all stop working at once. Nothing in `LogoutEndpoint` changed: its existing
`SignOutAsync(IdentityConstants.ApplicationScheme)` now deletes the session. `SecurityStampCookieValidator`
signs a refused cookie out the same way, so a cookie that a stamp change killed loses its row on its next
request.

The scheme's handler is `SignInSessionCookieHandler`, a sealed subclass of the framework's
`CookieAuthenticationHandler`. It changes the sign-in and the sign-out (decisions 5 and 6) and nothing
else. `Program.cs` adds the scheme with `AddScheme` and the one post-configure line `AddCookie` would add,
because `AddCookie` cannot name a handler.

Other devices have their own rows, so "Wyloguj" still ends this device only (#931).

### 2. The store is a table in the auth database

`authsystem."SignInSessions"`: `Id` (the key), `UserId` (a foreign key to `Users`, with its index),
`ProtectedTicket` and `ExpiresAt`. It is one new table, so the migration only adds (ADR-0023). The store
must survive a restart and a deploy and be one store for every instance, which the database already is.

The store is a singleton, because the cookie options hold one instance. Every call opens its own DI scope
and its own `AuthDbContext`, so the store never saves changes that belong to the request.

### 3. The stored ticket is encrypted with Data Protection, under its own purpose

The ticket holds the user's id, name, e-mail and roles, so it is never stored in the clear: the store
serializes it with `TicketSerializer` and protects it with Data Protection, the way the cookie was
protected before. The purpose is not the cookie's own, so a stored ticket can never be pasted in as a
cookie. A copy of the database alone gives neither the personal data nor a working cookie: a cookie still
has to be encrypted with the keyring.

A row that cannot be decrypted any more reads as no session. The cookie handler then answers as it does
for a cookie it cannot open, and the user signs in again. Nothing can ever read that row again, so the
store deletes it at once and logs a warning, because the cookie that named the row was opened with the
same keyring.

### 4. A row lives exactly as long as its cookie

- `ExpiresAt` is the ticket's own expiry. The cookie handler always sets it before it stores a ticket: 30
  days with "Zapamiętaj mnie", 30 minutes without.
- When the cookie slides, the handler calls `RenewAsync`, which moves `ExpiresAt`. Renewing is an update
  and never an insert, so a session that a sign-out in another tab deleted stays deleted.
- The handler deletes an expired row when its cookie comes back. A cookie that never comes back (a closed
  browser, a cleared cookie jar) would leave its row forever, so
  `SignInSessionPruneService` deletes every row whose `ExpiresAt` has passed once a day. It follows the
  pattern of `OpenIddictPruneService` (PERF-02). A row is expired by the handler's own rule: its expiry is
  in the past. `ExpiresAt` has no index: the table holds one row per live sign-in, the prune runs once a
  day, and every slide would also have to update the index.
- `RetrieveAsync` and `RemoveAsync` ignore the request's cancellation token. A sign-out reads the session
  and then deletes it, and a browser that leaves before the answer must not keep its session alive.
- A sign-in's insert can land while its answer is lost, and EF's retry then sends the same id again. The
  id is new for every sign-in, so the store reads that primary-key clash as the insert that landed, the
  same case #962 handled for the erasure.

### 5. Every sign-in starts a new session

Before it signs in, the framework's cookie handler reads the cookie the browser already has. While that
cookie's session lives, it does not store the new sign-in under a new key: it renews the old row and keeps
the old key (aspnetcore#22135). Every copy of the old cookie would then hold the new sign-in:

- A copy that a password change killed would work again as soon as the victim signs in on that browser.
  The reset page links straight to the login form.
- On a shared computer, a copy of the first user's cookie would be signed in as the second user, and the
  row would still carry the first user's id, so the erasure and #970 would miss it.

So `SignInSessionCookieHandler` deletes the session that the browser's current cookie names, and only then
lets the framework sign in. The framework finds no session and stores the new sign-in under a new key.
Because this lives in the handler, every sign-in path gets it, today's login page and any later one, with
no convention to remember. Signing out first would not work: the framework keeps the old key after its own
sign-out and would renew the row it had just deleted, so the new cookie would name nothing.

The handler opens the cookie the way the framework does, TLS token binding included. The framework keeps
the session key's claim type private, so the handler repeats it, and `SignInSessionTests` pins it against
a real cookie. When something earlier in the same request has already read the cookie, the framework keeps
the old key anyway, the new cookie names no session, and the user signs in again. That fails closed, and
nothing reads the cookie before a sign-in today.

### 6. A sign-out always clears the browser's cookie

The framework clears the cookie only after the store has read and deleted the session. A database error
there would answer with a 500 and leave this browser signed in, and the next person at a shared computer
would get the session. So `SignInSessionCookieHandler` catches a failed sign-out, logs it as an error and
clears the cookie anyway, like the best-effort revoke in `LogoutEndpoint` (#931). The row then lives until
it expires, and only a copy of the cookie could still use it.

### 7. The final account erasure deletes the user's rows

The stored ticket is personal data, so `AccountErasureService` deletes every row of the user. It does
that before the anonymizing save, not in the best-effort cleanup after it. The finalizer retries an
account only until that save lands, so a delete that came after it and failed would never run again.
A failed delete fails this run, and the next run tries again. The delete runs only while the account still
waits for its erasure, by the rule the emergency lock uses: an owner who cancelled meanwhile may already
have signed in again.

### 8. Rollout: a cookie from the other side of the deploy sends the user to the login form

- A cookie written before this change has no session key. The cookie handler refuses it ("SessionId
  missing"), and `connect/authorize` sends the browser to the login form. Every signed-in user types the
  password once. There are no real users yet, so that is fine.
- A cookie written after this change, read by an older build (a rollback), has a principal with only the
  session key and no user id. `SecurityStampCookieValidator` finds no user for it, refuses it and clears
  it, and the user sees the login form again.

Neither direction ends on an error page. `SignInSessionTests` pins the first one.

## Consequences

### Positive

- Signing out ends every copy of this browser's cookie at once, and other devices stay signed in.
- The cookie no longer carries the name, the e-mail, the roles or the security stamp. It is also much
  smaller.
- "Sign out of every device" (#970) can delete every row of a user in one statement. The stamp check
  stays as the second layer.
- A stolen database backup gives no working cookie and no readable personal data from this table.

### Negative / Accepted Trade-offs

- One row read on every request that reads this cookie: `connect/authorize`, `connect/logout` and the
  login POST. `connect/authorize` already reads the user for the stamp check, so it needed the database
  before. The other account pages do not read this cookie: the default scheme is the OpenIddict token,
  not the cookie.
- One insert per sign-in, plus a delete when the browser still held an older session; one update per
  slide (at most about once per half of the cookie's lifetime); and one delete per sign-out.
- We subclass the framework's cookie handler and repeat two of its details it does not publish: the
  session key's claim type and how it opens the cookie. One test pins the claim type against a real
  cookie, and the sign-in-over-an-old-cookie tests fail if the handler stops ending the old session, so
  an upgrade that changes either one fails the tests, not production.
- An older request from the same browser that slides its cookie after the login POST answered can set the
  old key back in the browser. The user then signs in once more. Nothing comes back to life, because a
  slide only updates a row and the old row is gone.
- A sign-out that hits a database error clears the browser's cookie but leaves the row until it expires.
  It is logged as an error.
- A new kind of server-side state with personal data. It is encrypted, pruned when it expires and
  deleted at erasure. It holds what the account held at sign-in, so after an e-mail change a row can
  still hold the old address, until its cookie comes back (the stamp check then deletes it) or it expires.
- A row whose cookie never comes back stays until it expires: at most 30 days, then the daily prune
  deletes it.
- A row that a stamp change killed stays until its cookie comes back or it expires. Nobody can use it,
  because the stamp check refuses it. #970 ("sign out of every device") is the ticket that deletes every
  row of a user at once, and the flows that change the stamp can call it then.

## Alternatives Considered

### A. Keep the sessions in memory

Rejected. Every deploy and restart would sign everyone out, and two instances (a rollout, a second
replica) would not see each other's sessions.

### B. Keep the sessions in an `IDistributedCache`

Rejected. Redis would be new infrastructure on the box for one table's worth of data. A database-backed
cache needs another package and stores an opaque value under a key, with no `UserId`, so the erasure and
#970 could not find the sessions of a user without decrypting every row.

### C. Keep a list of signed-out cookies and refuse them

Rejected. The cookie would still carry the personal data, each entry would have to live as long as the
cookie it blocks, and a missed entry would fail open. A store fails closed: no row, no session.

### D. Change the security stamp on sign-out

Rejected. It ends the sessions on every device, which is what #931 decided against.

### E. Shorten the cookie's lifetime

Rejected. It makes the window smaller but does not close it, and a remembered sign-in is a feature of the
product.

## Implementation Notes

- `src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/Sessions/SignInSession.cs`,
  `Configurations/SignInSessionConfiguration.cs`, `Migrations/*_AddSignInSessions.cs` — the table.
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Services/Sessions/SignInSessionCookieHandler.cs` — a new
  session on every sign-in, and a sign-out that always clears the cookie; `Program.cs` adds the scheme.
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Services/Sessions/SignInSessionTicketStore.cs` — the store;
  `Program.cs` sets it on the cookie options, `ApiDependencyInjection.cs` registers it.
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Services/Maintenance/SignInSessionPruneService.cs` — the
  daily prune; the integration host removes it like the other clock-driven jobs (#821).
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Services/Gdpr/AccountErasureService.cs` — the delete
  before the anonymizing save.
- Tests: `Tests/Auth/SignInSessionTests.cs` (a copy after sign-out, two devices, a password change, a
  sign-in over an older cookie of the same user and of another user or over one with no live session, a
  cookie from before the change, the cookie's contents, the stored ticket, a lost insert answer, a
  sign-out on a database error, and an expired and a sliding cookie through the real handler),
  `Tests/Maintenance/SignInSessionPruneServiceTests.cs`, and three cases in
  `Tests/Auth/AccountDeletionFinalizerTests.cs`.

## References

- Tickets #1013 (this change), #931 (sign-out ends this device only), #970 (sign out of every device),
  #963 (other devices keep this cookie after a normal sign-out, by design), #282 / SEC-03 (the stamp
  check).
- ADR-0023, ADR-0031.
- dotnet/aspnetcore#22135, why a sign-in renews the session of the cookie the browser already holds:
  https://github.com/dotnet/aspnetcore/issues/22135
- ASP.NET Core, `ITicketStore` and `CookieAuthenticationOptions.SessionStore`:
  https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.cookies.cookieauthenticationoptions.sessionstore
