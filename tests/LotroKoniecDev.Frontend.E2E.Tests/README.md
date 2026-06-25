# LotroKoniecDev.Frontend.E2E.Tests

Browser end-to-end tests for the Blazor SSR Frontend, driven with **Playwright + xUnit + Shouldly**
over a full stack that the test **owns and boots itself via Testcontainers** — no `docker compose`,
no shell scripts, no FluentDocker. Rationale + topology: **[ADR-0009](../../docs/adr/0009-browser-e2e-playwright-via-testcontainers.md)**.

## How it works

`PlaywrightStackFixture` (one xUnit collection fixture) boots, on a single private Docker network:

```
postgres ─ migrator ─ auth-api ─ tms-api ─ frontend ─ mailpit ─ playwright(browser)
```

- All app services get a DNS alias and serve **HTTPS** (`https://auth-api:8443`, `https://frontend:8443`).
- The **browser is a container too** — a Playwright `run-server` container we build by hand (the
  `Testcontainers.Playwright` module hard-codes a `localhost` readiness probe and omits run-server's
  `--host`, both broken on Playwright v1.55+ images; see `PlaywrightStackFixture.StartBrowserAsync`).
  The host test connects to it over WebSocket (`Chromium.ConnectAsync(ws://host:mappedPort/)`). Because the
  browser and the Frontend's OIDC back-channel are on the **same** network, `https://auth-api:8443`
  resolves identically for both — so the single-`Authority` OIDC constraint (ADR-0006) holds in-network.
- HTTPS is mandatory (the OIDC correlation cookie is `SameSite=None`). The cert is **generated in C#**
  (SAN: the service names); the back-channel services trust it via an inline root entrypoint.
- **Mailpit** receives the confirmation e-mail, so registration is genuine (not auto-confirmed) and the
  confirm-link step is real. The host test reads the link via Mailpit's HTTP API.

Because `ConnectAsync` drives an **in-container** browser, the host needs **no** `playwright install`
(no browser download) — only the NuGet driver, restored on build.

## Running locally

```bash
# Requires a running Docker daemon. First run builds 4 images + pulls the Playwright image (~2 GB).
dotnet test tests/LotroKoniecDev.Frontend.E2E.Tests

# Reuse already-built app images (skip the in-fixture docker build):
SKIP_DOCKER_BUILD=true dotnet test tests/LotroKoniecDev.Frontend.E2E.Tests
```

No Docker → the suite fails fast (it cannot boot the stack); it is **off the PR/CI gate by name**
(`*.E2E.Tests`), running only via `.github/workflows/e2e.yml` (`workflow_dispatch`) or a local run.

## Locator strategy

Per Playwright's guidance: `GetByRole` / `GetByLabel` first (the form labels and the nav buttons/links),
with `data-testid` **only** where there is no stable accessible name — the two consent checkboxes and the
post-action state panels (`register-success`, `confirm-email-success`). See `Infrastructure/Locators.cs`.

## Flows covered

1. Register → confirm e-mail (Mailpit link) → login (FE OIDC) → logout.

> PL-only: the Auth pages and the FE nav are Polish-only (no culture routing), so — unlike
> TheKittySaver's PL/EN matrix — there is no culture parameter.
