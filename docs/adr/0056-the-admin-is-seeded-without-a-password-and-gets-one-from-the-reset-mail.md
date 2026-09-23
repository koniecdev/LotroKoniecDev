# ADR-0056: The Admin Is Seeded Without a Password and Gets One From the Reset Mail

**Status:** Accepted
**Date:** 2026-09-23
**Decision-makers:** Solo maintainer (ticket #696)
**Related:** AuthSystem.API (`Extensions/DatabaseSeederExtensions.cs`, `Pages/Account/Login.cshtml.cs`),
`compose.hetzner.yaml`, `compose.prod.yaml`, runbook → "Admin account — first sign-in and rotation",
ADR-0022 (the admin logs in by e-mail), ADR-0038 (password reset through the outbox), ADR-0049
(a reset revokes sessions), ADR-0050 (where logs are stored), tickets #210, #689, #695, #696

## Context

The auth seeder creates one admin account on startup from `AdminUser:Email`, `AdminUser:Username` and
`AdminUser:Password`. On a box those came from `AUTH_ADMIN_*` in `/opt/lotro/.env`, so the admin
password sat in that file as plain text. The seeder skips an admin that already exists, so the value
also stayed in the file after anyone changed the password in the product. Nothing ever asked the
operator to take it out.

Code facts that shaped this decision:

- **No page grants the `Admin` role.** The seeder's `AddToRoleAsync` is the only place in `src/` that
  gives it. So the answer cannot be "stop seeding the admin".
- **The product already handles an account with no password.** Identity's `VerifyPasswordAsync` fails
  when the hash is null, so the login page and the password grant refuse it. The reset flow works for
  it with no change: `PasswordResetRequestProcessor` checks only that the user exists, that no deletion
  is scheduled and that an address is set, and `ResetPasswordAsync` does not care whether a hash
  existed. A cancelled deletion and a reverted e-mail change already leave accounts in this state and
  send them through the same reset.
- **Every box runs `ASPNETCORE_ENVIRONMENT=Production`**, set as a literal in both compose files. The
  in-process integration suite and the auth host in both E2E stacks run `Testing`; the host dev loop
  runs `Development`.
- **Logs leave the box.** The box agent tails stdout into Loki on the staging box, which keeps them
  for 14 days (ADR-0050 §7, §8).

## What this does and does not protect

This matters more than the mechanism, so it comes first.

**It does not stop anyone who can read `/opt/lotro/.env`.** The same file holds the OpenIddict signing
key and the Neon connection strings. With the signing key a reader mints an access token with the
`Admin` role, and the TMS API accepts it: tokens are signed, not encrypted. With the connection
strings a reader writes `PasswordHash` or `UserRoles` directly. Neither path touches the admin password,
and a second factor for the admin (#689) would not close them either. Protecting that file is the
box's job (`chmod 600`, the deploy user, SSH keys), not this decision's.

**What it does buy:**

1. **The admin password exists in exactly one place: the admin's head or password manager.** It is not
   in the `.env`, in copies of that file, in the `.env.XXXXXX` temp file `deploy.sh` writes, in a
   handover note, or in a value reused between staging and prod. The staging admin is handed to
   external testers, so a password shared between the two boxes is a real way to lose prod.
2. **The rotation is the normal product flow.** A reset changes the hash, revokes every session
   (ADR-0049), and needs no SQL. The old runbook said to delete the admin row and reseed, which also
   signed everyone out and gave the admin a new id.
3. **Code enforces it, not memory.** A fresh deploy cannot produce an admin whose password came from
   configuration, whatever the `.env` says.

## Decision

### 1. Outside Development and Testing the admin is created without a password

`SeedAdminUserAsync` calls `UserManager.CreateAsync(user)` without a password. It still sets
`EmailConfirmed` and the `Admin` role, and logs event `2351`. Only the e-mail is required now; a blank
e-mail still skips the seed.

### 2. `AdminUser:Password` is read only in Development and Testing

The host dev loop and every test suite keep a known admin login. Any other environment ignores the
value and logs warning `2352`. This is an allow-list, so a future `Staging` environment name is covered
too. Both compose files stop passing `AdminUser__Password` at all, so the warning fires only when
someone wires the value in by hand. It is **not** the migration nudge for existing boxes: once compose
stops mapping it, a stale `AUTH_ADMIN_PASSWORD` line is invisible to the app. The runbook carries that
step instead.

### 3. The mailbox is the bootstrap channel

The operator sets the first password through `/Account/ForgotPassword`, the same four steps as every
later rotation (runbook → "Admin account"). The admin account is exactly as strong as its mailbox,
which was already true: whoever reads that inbox could always reset the password.

### 4. A password-less account costs the login page as much time as any other

`CheckPasswordAsync` returns at once when there is no hash. Without a guard, a wrong password against
the not-yet-bootstrapped admin would answer faster than against any other account, and that would tell
a prober the address is a live account. The login page hashes a dummy password first when
`HasPasswordAsync` is false, as it already does for an unknown user and a locked-out one.

### 5. A taken username is logged, not only skipped

A typo in `AUTH_ADMIN_EMAIL` now produces an admin nobody can reach. Correcting the `.env` does not
replace it, because the username is taken and the seeder skips. The seeder logs warning `2353` in that
case, so the runbook's fix (delete the row, restart) has a signal to point at.

## Consequences

### Positive

- AC 1 of #696 holds by construction: no deployed environment can seed an admin whose password is
  readable from configuration.
- The rotation procedure is the product's own reset, tested end to end in
  `AdminSeedingTests.SeedAuthDatabase_OutsideDevelopmentAndTesting_AdminSignsInAfterSettingPasswordThroughReset`.
- No new endpoint, page, table or configuration key.

### Negative / Accepted Trade-offs

- **Bringing up the admin now depends on working mail**: outbox, RabbitMQ and Brevo. There is no
  break-glass channel short of SQL. Accepted: a box whose mail does not work cannot register a single
  translator either, so the runbook makes "prove delivery first" the first step instead of adding a
  second channel.
- **Existing boxes are not fixed by the deploy.** Their admin rows keep the password from the `.env`
  until the owner rotates it and deletes the line (runbook → "Migrating a box seeded before #696").
  Only the owner can do that: it needs the admin mailbox and write access to the box.
- **The parity stack needs Mailpit for its first admin sign-in** (`--profile local-smtp`).

## Alternatives Considered

### A. A random password, printed once to the startup log

Rejected. The log is not a private channel: stdout goes to Loki on the staging box and stays there for
14 days (ADR-0050), so a prod admin password would live on the other box. It also needs a way to force
a change at first sign-in, which is alternative B.

### B. Keep a bootstrap password and force a change at first sign-in

Rejected. Identity has no "must change password" state. It would need a new flag on the user, a new
page, and a guard on every sign-in path. The mailbox reset already gives the same result with code
that exists and is tested, and it never puts a password in the `.env` at all.

### C. Runbook only: document the rotation and leave the seeder alone

Rejected. Rotation through the product was already possible, but a fresh deploy would still write the
password into the `.env` and seed it, which is exactly what AC 1 of #696 rules out. A blank password
does not help either: the old seeder then created no admin at all, and nothing else can grant the role.

### D. Fail startup when `AdminUser:Password` is set outside Development and Testing

Rejected. The boxes seeded before #696 still carry the line in their `.env`. Once compose stops mapping
it the check could never fire there, and anywhere else a hard failure would turn a leftover line into
an outage. A warning says the same thing without taking the site down.

## Implementation Notes

- `DatabaseSeederExtensions.ReadBootstrapPassword` is the one place that decides whether a configured
  password is used. It checks `IsDevelopment()` and the existing `IsTesting()` extension.
- Event ids `2351`–`2353` sit in the Startup range of `EventIds.cs`.
- The integration tests seed with a stub `IWebHostEnvironment` (`Production`, `Staging`) against the
  Testing host. The cleaner does not truncate `OpenIddictApplications`, so the reseed leaves the test
  client in place.
- `appsettings.json` no longer carries an `AdminUser:Password` key; `appsettings.Development.json` and
  `appsettings.Local.json.example` still do.

## References

- Ticket #696 (SEC-17). Tasks 2 (a second factor for the admin) and 3 (an alert on an admin sign-in
  from a new address) moved out: see #689 and #695.
- Runbook → "Admin account — first sign-in and rotation", "Reseed traps".
