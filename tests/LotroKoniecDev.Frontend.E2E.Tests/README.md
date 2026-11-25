# LotroKoniecDev.Frontend.E2E.Tests

Browser end-to-end tests for the Blazor SSR Frontend, driven with **Playwright + xUnit + Shouldly**
over a full stack that the test **boots itself via Testcontainers**: no `docker compose`, no shell
scripts, no FluentDocker. Rationale and topology: **[ADR-0009](../../docs/adr/0009-browser-e2e-playwright-via-testcontainers.md)**.
The rules shared with the TMS E2E suite (off the PR gate, images by ID) are in
[`tests/CLAUDE.md`](../CLAUDE.md).

## How it works

`PlaywrightStackFixture` (one xUnit collection fixture) boots, on a single private Docker network:

```
postgres ─ migrator ─ rabbitmq ─ auth-api ─ tms-api ─ frontend ─ mailpit ─ playwright(browser)
```

- Every app service has a DNS alias and serves **HTTPS** (`https://auth-api:8443`, `https://frontend:8443`).
- The **browser is a container too**: a Playwright `run-server` container built by hand, because
  the `Testcontainers.Playwright` module hard-codes a `localhost` readiness probe and leaves out
  run-server's `--host`, and both break on Playwright v1.55+ images (see
  `PlaywrightStackFixture.StartBrowserAsync`). The host test connects to it over WebSocket
  (`Chromium.ConnectAsync(ws://host:mappedPort/)`). The browser and the Frontend's OIDC
  back-channel are on the **same** network, so `https://auth-api:8443` resolves the same way for
  both, and the single-`Authority` OIDC constraint (ADR-0006) holds.
- HTTPS is required, because the OIDC correlation cookie is `SameSite=None`. The cert is
  **generated in C#** (SAN: the service names), and the back-channel services trust it via an
  inline root entrypoint.
- **Mailpit** receives the confirmation e-mail, so registration is real (not auto-confirmed) and so
  is the confirm-link step. The host test reads the link through Mailpit's HTTP API.

`ConnectAsync` drives a browser **inside a container**, so the host needs **no**
`playwright install`, only the NuGet driver, restored on build.

## Running locally

```bash
# Requires a running Docker daemon. First run builds 4 images + pulls the Playwright image (~2 GB).
dotnet test tests/LotroKoniecDev.Frontend.E2E.Tests

# Reuse the app images this worktree built last time (skip the in-fixture docker build):
SKIP_DOCKER_BUILD=true dotnet test tests/LotroKoniecDev.Frontend.E2E.Tests
```

The images are tagged per worktree as `<repository>:fe-e2e-<folder>-<hash>`: the suite name, the
worktree's folder name, and 8 hex characters of a hash of the solution path (#884). The TMS suite
uses the `e2e-` prefix. So two worktrees can run the suite at the same time, and each run tests its
own code. Never run the same suite twice at the same time in **one** worktree: the second build
takes the tag, and the first run fails with `pull access denied for sha256` (#889).

A run leaves one image set per worktree behind. When you delete a worktree, remove its images by
hand. Copy `<folder>-<hash>` from the first listing:

```bash
docker image ls --filter label=lotrokoniecdev.e2e --format '{{.Repository}}:{{.Tag}}'    # all worktrees
docker image ls --filter label=lotrokoniecdev.e2e --format '{{.Repository}}:{{.Tag}}' \
  | grep -F -- '-<folder>-<hash>' | xargs -r docker image rm                             # one worktree
```

Without Docker the suite fails fast, because it cannot boot the stack. It is **off the PR/CI gate
by name** (`*.E2E.Tests`) and runs through `.github/workflows/e2e.yml` (job `e2e-frontend`): by hand
with `workflow_dispatch`, on its own only on Dependabot PRs that touch `Directory.Packages.props`,
`.config/dotnet-tools.json` or a Dockerfile (CI-03 / #433), or as a local run.

## Locator strategy

Per Playwright's guidance: `GetByRole` / `GetByLabel` first (the form labels and the nav
buttons/links), with `data-testid` **only** where there is no stable accessible name: the two
consent checkboxes and the post-action state panels (`register-success`, `confirm-email-success`).
See `Infrastructure/Locators.cs`.

## Flows covered

One test class per flow, in `Flows/`:

- `RegisterConfirmLoginLogoutTests`: register → confirm e-mail (Mailpit link) → login (FE OIDC) →
  logout.
- `AuthPagesContentSecurityPolicyTests`: the auth pages break no rule of their own CSP.
- `OneTimeLinkBackTests`, `OneTimeLinkReloadTests`, `OneTimeLinkDoubleClickTests`: a used one-time
  link never shows a live form again.
- `SubmitOnceButtonTests` and `ChangeEmailSuccessStyleTests`: the busy button and the success
  style of the e-mail change page.
- `CookieConsentBannerTests`, `SelfHostedFontsTests`, `AccountGdprSelfServiceTests`: the LEGAL
  pack (cookie banner, no Google font hosts, data export and account deletion under "Moje konto").
- `HomeDiscordBannerTests`: the home page's Discord band sits right on top of the footer on short and
  tall screens, and keeps its own width on a screen wider than the strip.
- `HomeLayoutTests`: the home page's player and translator boxes have the same height side by side,
  the big percentage is centred against its text, and the gap between the boxes is 24 px side by side
  and 20 px stacked on a phone.
- `PageWidthTests`: the page is as wide as the window up to 1920 px and centred on a wider one; the
  terms document and the home page's risk note keep the width they had under the old 1400 px limit
  (#1572).
- `TranslationsFilterLayoutTests`: the translation list's filter row keeps every control inside the
  form and the search and status fields wide enough on every desktop width, shares one row on a wide
  screen, and stacks its fields on a phone.
- `TranslationListWayBackTests`: search + status + page 2 on the list → "Edytuj" → save →
  "Wróć do listy" shows the same list (#1452).
- `TranslationListInlineSaveTests`: "Edytuj tutaj" on a row below the fold → save → the same list,
  scrolled to the saved row below the navbar; a refused save stays in its open row; a row the save
  took out of the status filter leaves a note in its place; bulk approve still works (#1493).
- Both list flows get their rows from `TranslationRowsSeed` (`PlaywrightStackFixture.TranslationRows`):
  the first flow that needs them signs in as the admin the auth host seeds
  (`PlaywrightStackFixture.Admin`), registers a game version and uploads one `exported.txt` built in
  memory. An import removes every row it does not carry, so the flows share that one import, and each
  flow's English carries a token no other flow uses (ADR-0009 §5 amendment).

> PL-only: the Auth pages and the FE nav are Polish-only (no culture routing), so there is no
> culture parameter.
