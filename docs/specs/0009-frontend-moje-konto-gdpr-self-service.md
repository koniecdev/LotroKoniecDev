# Spec 0009: Frontend "Moje konto" — GDPR self-service (export, delete, change password)

- **Status:** Implemented
- **Date:** 2026-07-11
- **Author:** koniecdev
- **Ticket:** #457 (LEGAL-02) — part of the legal & GDPR pack (epic #459)
- **Related:** #452/PR#460 (LEGAL-01 two-phase deletion, ADR-0031), auth privacy policy
  `Pages/Account/PrivacyPolicy.cshtml` §06, TKS mirrors: `Components/Pages/Account.razor`,
  `DeleteAccount.razor`, `AccountDeletionScheduled.razor`, `ChangePassword.razor`,
  `Infrastructure/Account/AccountExportEndpointsExtensions.cs`; our siblings:
  `Components/Pages/ImportExport/ImportExportEndpointsExtensions.cs` (download proxy),
  `Components/Pages/Dashboard/DashboardStatsLoader.cs` (loader seam)

## Business context

The privacy policy promises translators self-service GDPR rights: *"Eksport oraz usunięcie konta
uruchomisz samodzielnie w sekcji „Moje konto""* — but the frontend has no account section at all.
LEGAL-01 (#452) shipped the entire backend: authenticated data export with state-aware HATEOAS
links, two-phase deletion with a 14-day grace window (`X-Deletion-Scheduled-At` /
`X-Deletion-Finalizes-At` response headers), and the anonymous auth-side cancel page driven by the
emailed one-time token. This ticket builds the browser UX on top, so the policy's promise becomes
reachable.

## Goal

A signed-in translator can view their account data, download their full data export as a JSON
file, change their password, and schedule account deletion — entirely from the browser; the
deletion confirmation shows the exact finalization date, and cancellation works through the
emailed link (auth-side page, already shipped).

## In scope

- **New typed auth client** `Infrastructure/HttpClients/AuthSystemHttpClients/`
  (`IAuthSystemClient` + `AuthSystemClient` + `AuthContentNegotiationAndAuthDelegatingHandler`),
  mirroring the TMS client: base address from `AuthSystemSettings.BaseUrl`, HATEOAS Accept header,
  bearer token from the OIDC session, dead-session backstop on 401. Registered in
  `HttpClientsDependencyInjectionExtensions.AddHttpClients()`.
- **Auth discovery** on `IDiscoveryCache`: `GetAuthSystemDiscoveryAsync()` (auth-state-keyed cache
  entry, same non-caching-of-failures discipline as the TMS one). Auth discovery root advertises
  `export-account-data` for authenticated users (`DiscoveryLinkFactory`).
- **`ApiResult` additions** (mirror TKS): `IsUnauthorized` (401 detection so pages can
  `RedirectToLogin`), and `PostForHeadersApiResultAsync` returning selected response headers
  (needed for `X-Deletion-Finalizes-At`).
- **`/account` page** (`Components/Pages/Account/Account.razor` + `AccountLoader.cs`, static SSR,
  `[Authorize]`, `[StreamRendering]`): shows username, e-mail (+ confirmed badge), roles, consent
  states with dates (`AuthDataExportDto`); action rows for data export (download link), change
  password, delete account (gated on the HATEOAS rels from the export envelope). No
  resend-confirmation row: Identity runs with `RequireConfirmedEmail = true`, so a signed-in user
  always has a confirmed e-mail — the rel can never appear for a browser session.
- **Export download** `GET /account/export` (server endpoint in
  `Components/Pages/Account/AccountEndpointsExtensions.cs`, `RequireAuthorization()`): proxies the
  auth export (discovery → `export-account-data` link → typed client), serializes the envelope as
  indented camelCase JSON, serves it as `lotro-translator-moje-dane-<yyyyMMdd-HHmmss>.json`.
  Razor SSR pages cannot return file results — same reasoning as the polish.txt download proxy.
- **`/account/delete` page** (`DeleteAccount.razor`, `[Authorize]`): consequences list, password +
  confirmation-phrase (`USUWAM`) form → POSTs `DeleteAccountRequest` to the `delete-account` rel →
  on 204 reads `X-Deletion-Finalizes-At` from headers → success state with an explicit
  "Przejdź dalej" POST button to the **new local-only sign-out endpoint** with
  `returnUrl=/account/deletion-scheduled?until=<iso>`. (TKS auto-submits that form with an inline
  script; our CSP is locked to `script-src 'self'`, so the explicit button replaces the script.)
- **Local sign-out** `POST /auth/local-signout` in `AuthEndpointsExtensions`: cookie sign-out only
  + redirect to a validated local `returnUrl`. Needed because the upstream session is already dead
  (tokens revoked, account locked) — RP-initiated end-session would bounce through a dead auth
  session.
- **`/account/deletion-scheduled` page** (anonymous): parses the tamperable `until` query
  defensively, shows the finalization date in Europe/Warsaw or the generic 14-day phrasing,
  explains lockout / cancel-by-email / password-reset-after-cancel.
- **`/account/change-password` page** (`ChangePassword.razor`, `[Authorize]`, mirrors TKS): current
  password + new password (+ repeat) form → POSTs `ChangePasswordRequest` to the `change-password`
  rel; success state links back to `/account`. (User decision 2026-07-11: full page ships in this
  ticket, not just an entry point.)
- **Nav entry**: `NavMenu.razor`'s `<Authorized>` gains a "Moje konto" link to `/account` (policy
  wording) next to the username. No footer entry — the footer carries no links at all today, and a
  lone account link there would be odd; the ticket's "nav/footer" is satisfied by the nav.
- **Tests**: loader/endpoint unit tests + bUnit render tests (mirror
  `ImportExportEndpointsExtensionsTests` / `DashboardStatsLoaderTests` / page tests); browser E2E
  in `LotroKoniecDev.Frontend.E2E.Tests/Flows` — the delete→cancel round trip (register → confirm →
  login → schedule deletion from `/account` → assert locked-out + scheduled page → open Mailpit
  cancel link → auth-side cancel page → reset password → login again) and a data-export download
  assertion. `check-ssr-purity` stays green.

## Out of scope

- **Cancel-deletion UX in the frontend** — cancellation is anonymous by design (ADR-0031) and the
  auth-side `Pages/Account/CancelDeletion.cshtml` already shipped with #452; the emailed link is
  the only entry.
- **Roles editing, e-mail change, profile fields** — nothing beyond what `AuthDataExportDto`
  exposes today.
- **Localization** — the frontend is Polish-only (established repo-wide); TKS's `IStringLocalizer`
  layer is not lifted.
- **TMS-side data in the export** — the auth envelope (`IsComplete: true`) is the whole GDPR
  surface today; translations are attributed but public work product, not the user's personal data
  store. Revisit if a DPO review says otherwise.

## Business rules & edge cases

- **Affordances come from HATEOAS, not local role checks**: the export envelope's `Links` gate the
  delete/change-password/resend rows; discovery's `export-account-data` gates the whole page load
  (missing → "no access" error state, mirroring TKS).
- **Deletion already scheduled** (`DeletionScheduledAt` set → API suppresses all rels except
  `cancel-deletion`): `/account` shows a "deletion scheduled" notice instead of action rows;
  `/account/delete` shows the same and no form. In practice the session dies at scheduling time,
  so this state is mostly a race/backstop.
- **401 anywhere** (revoked mid-session): `RedirectToLogin`, mirroring TKS.
- **Confirmation phrase** is exactly `USUWAM` (ordinal match, enforced server-side in the SSR
  handler — a static `[RegularExpression]` would leak into validation summaries oddly; mirror TKS).
- **The `until` query is user-tamperable** → parse with `DateTimeOffset.TryParse` round-trip style;
  fallback copy: "w ciągu 14 dni".
- **Export endpoint failures** map upstream ProblemDetails through (502 fallback), exactly like
  the polish.txt proxy.
- **Change password**: new password rules are enforced by the auth API (`PasswordValidationRules`);
  the FE form only checks non-empty + repeat-match and renders API ProblemDetails on failure.
  After a successful change the session stays valid (Identity rotates the stamp server-side; the
  cookie refresh path recovers) — success UI just confirms and links back.

## Contract

- **FE routes:** `GET /account` (authorized page), `GET /account/export` (authorized file
  download), `GET/POST /account/delete` (authorized page + SSR form), `GET
  /account/deletion-scheduled?until=<iso>` (anonymous page), `GET/POST /account/change-password`
  (authorized page + SSR form), `POST /auth/local-signout` (cookie-only sign-out, local returnUrl).
- **Upstream:** `GET {auth}/` discovery → `GET {auth}/auth/account/data-export` →
  `POST {auth}/auth/account/delete` (204 + `X-Deletion-Scheduled-At`/`X-Deletion-Finalizes-At`) /
  `POST {auth}/auth/change-password` (204). All via `IAuthSystemClient` with the session bearer.
- **Download filename:** `lotro-translator-moje-dane-<yyyyMMdd-HHmmss>.json`, `application/json`,
  indented camelCase.

## Acceptance criteria

- [ ] Signed-in translator opens `/account` and sees username, e-mail (+confirmation state), roles
      and both consent dates from the live auth export.
- [ ] Clicking the export action downloads a JSON file containing the full
      `AccountDataExportResponse` envelope.
- [ ] `/account/delete` with the correct password + `USUWAM` schedules deletion; the browser lands
      (signed out) on `/account/deletion-scheduled` showing the date from
      `X-Deletion-Finalizes-At`; a wrong password or wrong phrase shows the error and schedules
      nothing.
- [ ] After scheduling, the account is locked out of login (E2E asserts the auth error page), and
      the Mailpit cancel link → auth-side cancel page → password reset → login round trip succeeds
      (browser E2E).
- [ ] `/account/change-password` with the correct current password changes it (old password stops
      working, new one logs in); wrong current password renders the API error.
- [ ] Signed-in nav shows the "Moje konto" entry; anonymous users never see it; `/account*`
      authorized routes redirect anonymous visitors to login.
- [ ] `scripts/check-ssr-purity.sh` passes; unit + bUnit suites green; E2E flow green (Docker).

## Open questions

- ~~Change-password: full page or entry point only?~~ → **Full page ships in this ticket**
  (user decision, 2026-07-11).

## Assumptions

- The auth access token issued to the frontend session is accepted by the auth API's own bearer
  validation (same OpenIddict server; `UserInfoEndpoint`/`ExportAccountData` already validate it).
- Polish-only copy, consistent with the rest of the frontend.
