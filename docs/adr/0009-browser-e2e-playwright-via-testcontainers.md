# ADR-0009: Browser E2E (Playwright) runs against an in-network Testcontainers stack, not a dedicated compose file

**Status:** Accepted
**Date:** 2026-06-24
**Decision-makers:** Solo maintainer
**Related:** M3 Frontend (Blazor SSR, OIDC RP), AuthSystem (OpenIddict + Identity Razor Pages),
`tests/LotroKoniecDev.TranslationSystem.E2E.Tests` (existing Testcontainers API-E2E),
`.docker/trust-ca-entrypoint.sh`, ADR-0002 (TheKittySaver 1:1 lift), ADR-0006 (single-`Authority`
OIDC; Frontend on host in dev), ADR-0008 (cloud-agnostic deployment / HTTPS parity), TheKittySaver
`tests/TheKittySaver.E2E.Frontend.Tests` + `compose.playwright.yaml` + ADR-0011 (the reference's
all-in-container variant)

## Context

M3 ships the Blazor SSR Frontend and the golden account loop (register → confirm e-mail → log in
via OIDC → log out) spanning **two origins** — the Frontend and the AuthSystem's own Identity Razor
Pages. We want a browser end-to-end test for that loop that needs **no seeded database and no
`exported.txt`**, runnable from a fresh clone.

Code facts that constrain the design:

- **Single-`Authority` OIDC (ADR-0006).** The RP collapses front-channel and back-channel onto one
  `options.Authority = settings.Authority`
  (`Frontend/Infrastructure/Auth/AuthenticationDependencyInjectionExtensions.cs:100`), with
  `GetClaimsFromUserInfoEndpoint = true` (so the FE *also* calls userinfo server-to-server). The
  browser redirect AND the FE back-channel (discovery, token, userinfo) must resolve that one URL to
  the **same** origin, and it must equal the token `iss` OpenIddict stamps absolutely
  (`OpenIddictExtensions.cs`). ADR-0006 secured this in dev by running everything on the host
  (`localhost:5003` resolves identically for browser and host RP). A browser E2E needs the same
  property in an automatable, headless-CI-friendly substrate.
- **HTTPS is mandatory.** The OIDC handler's correlation/nonce cookies are `SameSite=None`
  regardless of the authorize `response_mode` (`form_post` originally; `query` since #306), and a
  browser only sends `SameSite=None` cookies when `Secure`. Plain HTTP breaks the callback. So the stack must serve HTTPS, which means a cert the FE/tms back-channels **trust** —
  and .NET validates against the **OS trust store** (it ignores `SSL_CERT_FILE`; see
  `.docker/trust-ca-entrypoint.sh`).
- **We already orchestrate the full stack from C# with Testcontainers.**
  `TranslationSystem.E2E.Tests/E2ETestFixture` builds the auth/tms/migrator images and boots
  postgres + migrator + auth-api + tms-api on a private network via `ContainerBuilder` /
  `NetworkBuilder`. "Orchestrate the stack from C#" is already this repo's idiom — there is **no**
  compose file behind our E2E.
- **Testcontainers has an official Playwright module.** `Testcontainers.Playwright` runs a
  `mcr.microsoft.com/playwright` browser **in a container**, exposes a WebSocket endpoint
  (`GetConnectionString()`), and shares a network (`GetNetwork()`) so the in-container browser
  reaches other containers by DNS name. The documented split is "Docker Compose = CI-simplicity;
  **Testcontainers = programmatic control + better local dev**" (Playwright .NET / Testcontainers
  docs).
- **Locator guidance.** Playwright officially recommends user-facing locators —
  `GetByRole` → `GetByLabel` → `GetByText` — with `data-testid` as the explicit-contract fallback
  when an element has no stable accessible name. TheKittySaver put a `data-testid` on everything;
  `~/RiderProjects/unite-next` uses `getByRole`/`getByLabel`.
- **The reference (TheKittySaver) uses a separate `compose.playwright.yaml` + shell scripts**
  (all-in-container; the test runner itself is a container — ADR-0011). That is one valid topology,
  but it is a *second* orchestration idiom alongside our existing Testcontainers API-E2E.

## Decision

### 1. Unify all E2E on Testcontainers; drive the browser with the official Playwright module

The new `tests/LotroKoniecDev.Frontend.E2E.Tests` boots the whole stack — postgres + migrator +
auth-api + tms-api + **frontend** + mailpit + the `Testcontainers.Playwright` **browser** — from a
single `IAsyncLifetime` fixture, the same idiom as our API-E2E. The host test connects to the
in-container browser over WS (`Chromium.ConnectAsync(playwrightContainer.GetConnectionString())`) and
drives pages from there. The suite runs with **`dotnet test`** — no script to launch it.

### 2. The in-network DNS topology satisfies single-`Authority` OIDC

Every service joins one Testcontainers network with a DNS alias (`auth-api`, `tms-api`, `frontend`,
`mailpit`). The **browser is a container on that same network**, so `https://auth-api:8443` resolves
identically for the browser front-channel AND the FE back-channel — the exact property ADR-0006
secured on the host via `localhost`, here secured **in-network via DNS**. The seeded web-client
redirect URI is `https://frontend:8443/callback`, the issuer is `https://auth-api:8443`
(`OpenIddict__Issuer`), and `AuthSystem__Authority` is the same — so `iss` matches by construction.
No reverse proxy, no `MetadataAddress` split, no `host.docker.internal`.

### 3. HTTPS via a C#-generated cert; trust via an inline root entrypoint (no committed script)

The fixture generates a self-signed cert in C# (`CertificateRequest` + `SubjectAlternativeNameBuilder`,
SAN: `auth-api`, `tms-api`, `frontend`, `mailpit`, `localhost`; `CA:TRUE`) — no `openssl`, fully
cross-platform. auth/tms/frontend serve it on Kestrel (`https://+:8443`, plus `http://+:8080` for
container health probes only). tms-api and the Frontend trust it for their outbound back-channels via
an **inline** entrypoint — `WithEntrypoint("/bin/sh", "-c", …)` copies the mapped cert into
`/usr/local/share/ca-certificates`, runs `update-ca-certificates`, then `exec`s the app — so there is
**no committed shell script** (it mirrors the idea of the repo's `.docker/trust-ca-entrypoint.sh`
without mounting it). Because `update-ca-certificates` must write the OS trust store, the container runs
as **root** (`User = "0"`) and does **not** drop privileges — a deliberate simplification for this
ephemeral test stack, and the one way it diverges from the prod-parity entrypoint (which drops back to
the non-root app user). Mailpit receives the confirmation e-mail (`Email__Host=mailpit`), so
registration sends a real confirmation link instead of auto-confirming.

### 4. Locators: role/label first, `data-testid` only as a state-panel contract

Inputs and buttons are reached by `GetByLabel` / `GetByRole` (the lifted Auth Razor Pages already
associate `<label asp-for>` with their inputs). `data-testid` is added **only** where there is no
stable accessible name: the success panels (`register-success`, `confirm-email-success`) and the two
consent checkboxes on Register. This is the Playwright-recommended hybrid and keeps the edit to the
lifted markup minimal. AuthSystem is **not** frozen (only the patcher is), so these hooks are fine.

### 5. Scope: one no-seed flow, PL-only, off the PR gate

The first flow is **register → confirm (Mailpit link) → login (FE OIDC) → logout** — no DB seed, no
`exported.txt`. It is **PL-only**: our Auth pages and FE nav are Polish-only with no culture routing
(unlike TheKittySaver's PL/EN matrix), so there is no culture parameter. The project is named
`*.E2E.Tests`, so `pr-verify`/`ci` skip it by the existing convention; it runs via `e2e.yml`
(`workflow_dispatch`) and local `dotnet test`, exactly like our API-E2E. Wiring it onto a routine
gate is deferred (cost; mirrors TheKittySaver's deferral).

### 6. Reject FluentDocker and reject a dedicated `compose.playwright.yaml`

**FluentDocker** is not added: its only edge over Testcontainers is reading a *compose file* from
C#, but the Playwright module removes the need for a compose file at all, so it would be a third
orchestration tool doing what Testcontainers already does (YAGNI / single-idiom). A dedicated
**`compose.playwright.yaml`** is likewise not created — it would reintroduce a second orchestration
surface that the module makes redundant.

## Consequences

### Positive

- **One orchestration idiom across all E2E** (API + browser) — Testcontainers, no compose, no bash,
  no FluentDocker. Defensible: "the browser is just another container on the same network, via the
  official Testcontainers Playwright module."
- **`dotnet test` from a fresh clone** (needs only Docker, which our API-E2E already requires) — the
  best "works from boot" ergonomics; runnable + debuggable from the IDE.
- **Prod-parity HTTPS + single-`Authority` OIDC** validated end-to-end through a real browser, with
  the cert trust inlined into the container entrypoint (no committed script).
- **Locators follow official Playwright guidance**, touching the lifted Auth markup minimally.

### Negative / Accepted Trade-offs

- Testcontainers pulls the `mcr.microsoft.com/playwright` image (~2 GB) on first run; cached after.
- **Diverges from TheKittySaver's all-in-container compose substrate** (ADR-0011). Accepted: the goal
  here is the best, interview-defensible production pattern for *this* repo, which already
  standardized on Testcontainers — consistency with our own API-E2E beats 1:1 mirroring of the
  reference's *test substrate* (the slice/domain patterns are still mirrored 1:1 elsewhere).
- An in-container CA-trust step is still required (HTTPS is mandatory) — it is inlined into the
  container entrypoint (`WithEntrypoint`), so it adds no committed script, at the cost of running the
  container as root (no privilege drop), unlike the prod-parity `.docker/trust-ca-entrypoint.sh`.
- The browser runs headless only (no trace viewer UI mid-run); failures rely on Playwright tracing.

## Alternatives Considered

### A. Mirror TheKittySaver: `compose.playwright.yaml` + shell scripts, all-in-container

Rejected. Proven and CI-shaped, but it is a *second* orchestration idiom next to our Testcontainers
API-E2E, runs the test runner inside a container (no IDE debugging), and keeps the bash orchestration
the goal was to shed. FluentDocker would only replace one of its three scripts (cert + in-container
trust remain) while adding a redundant dependency.

### B. Testcontainers + official Playwright module, in-network browser (this ADR)

Chosen. Single idiom, `dotnet test`, official module, in-network DNS dissolves the OIDC constraint.

### C. `compose.playwright.yaml` driven from C# via FluentDocker

Rejected. Honours "a compose file driven from C#," but adds `Ductus.FluentDocker` overlapping
Testcontainers and still needs the cert + in-container-trust scripts — more surface for no gain.

### D. Host Frontend + host browser (ADR-0006 dev topology)

Rejected for automated E2E. It works for manual dev, but requires `dotnet run` of the FE on the host
(not a self-contained `dotnet test`), and headless CI would have to start and health-gate host
processes — exactly the orchestration Testcontainers does cleanly in-process.

## Implementation Notes

- New project `tests/LotroKoniecDev.Frontend.E2E.Tests` (NuGet-only, no `ProjectReference` — it
  drives everything over HTTP/the browser). Packages: `Microsoft.Playwright`,
  `Testcontainers.Playwright` (+ `Testcontainers`, xUnit, Shouldly, `Xunit.SkippableFact`, Bogus).
  Add `Microsoft.Playwright` + `Testcontainers.Playwright` to `Directory.Packages.props`; add the
  project to `LotroKoniecDev.slnx`.
- `PlaywrightStackFixture` (`IAsyncLifetime`): builds the auth/tms/frontend/migrator images
  (`SKIP_DOCKER_BUILD=true` to reuse pre-built), generates the cert, creates the network, boots
  postgres → migrator → auth-api → tms-api → frontend (+ mailpit + the Playwright module), health-
  gates each, exposes the browser WS + mapped Mailpit port. auth runs the `Testing` profile (seeded
  admin/clients) with `OpenIddict__Issuer`/`WebClient__RedirectUris__0` pointed at the in-network
  HTTPS origins and `Email__Host=mailpit`; tms/frontend run `Development` with their `AuthSystem` /
  `TranslationSystem` URLs overridden to the in-network HTTPS aliases.
- Markup: `data-testid` on `register-success`, `confirm-email-success`, and the two Register consent
  checkboxes (`Pages/Account/Register.cshtml`, `ConfirmEmail.cshtml`); FE nav reached by role.
- Flow: `RegisterConfirmLoginLogoutTests` + infra helpers (`PlaywrightStackFixture`, `E2EConfig`,
  `Routes`, `TestUser`, `MailpitClient`, `AuthActions`, `PlaywrightExtensions`, `Locators`,
  `E2ETestBase`, `E2ECollection`). `MailpitClient` polls subject `Potwierdzenie konta` for a link to
  `/Account/ConfirmEmail` (both already match the AuthSystem sender).
- CI: add the project to `.github/workflows/e2e.yml` (manual `workflow_dispatch`); it stays off
  `pr-verify`/`ci` by the `*.E2E.Tests` naming convention.

## References

- ADR-0006 — single-`Authority` OIDC; Frontend on host in dev (the constraint this ADR re-satisfies
  in-network)
- ADR-0002 — TheKittySaver 1:1 lift (slice/domain patterns; the test *substrate* deliberately
  diverges here)
- ADR-0008 — HTTPS parity + `.docker/trust-ca-entrypoint.sh` (the prod-parity trust pattern this stack
  mirrors inline, without mounting the script)
- Testcontainers for .NET — Playwright module (`https://dotnet.testcontainers.org/modules/playwright/`)
- Playwright — Locators / Best Practices (role/label first, test-id as explicit contract)
- TheKittySaver `tests/TheKittySaver.E2E.Frontend.Tests` + ADR-0011 (the all-in-container reference;
  Alternative A)
