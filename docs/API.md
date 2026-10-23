# LotroKoniecDev TMS — API reference for frontend & CLI integrators

> **The code is the source of truth.** When a route here disagrees with
> `TranslationSystem.API/Features/**` or `AuthSystem.API/Features/**`, read the slice and fix this doc.
> Companion docs: [DOMAIN.md](DOMAIN.md) (the model behind these endpoints),
> [INVARIANTS.md](INVARIANTS.md) (the rules they enforce), [auth-tutorial.md](auth-tutorial.md)
> (the sign-in setup end to end).

The TMS is **two backend services** that share one OpenIddict issuer. `auth-api` (the authorization
server) issues tokens. `tms-api` (a resource server) serves the translation domain and validates those
tokens. The Blazor SSR frontend (the OIDC client) and the patcher CLI are the clients.

## Table of contents

1. [Service topology](#1-service-topology)
2. [Authentication & authorization model](#2-authentication--authorization-model)
3. [Strongly-typed IDs and enums](#3-strongly-typed-ids-and-enums)
4. [Content negotiation: HATEOAS opt-in](#4-content-negotiation-hateoas-opt-in)
5. [Error contract (`ProblemDetails`)](#5-error-contract-problemdetails)
6. [Pagination](#6-pagination)
7. [AuthSystem endpoints (`auth-api`)](#7-authsystem-endpoints-auth-api)
8. [TranslationSystem endpoints (`tms-api`)](#8-translationsystem-endpoints-tms-api)
9. [Workflow primers](#9-workflow-primers)
10. [HATEOAS rel catalogue](#10-hateoas-rel-catalogue)
11. [Quick conventions checklist](#11-quick-conventions-checklist)
12. [Where to read more](#12-where-to-read-more)

---

## 1. Service topology

| Service | Role | Dev URL (host, HTTPS) | Container port |
|---|---|---|---|
| `auth-api` | OpenIddict authorization server + ASP.NET Identity user store | `https://localhost:5003` | `8080` |
| `tms-api` | Translation domain resource server (JwtBearer) | `https://localhost:5002` | `8080` |
| Frontend (Blazor SSR) | OIDC client | `https://localhost:7017` | `8080` |

In dev, `compose.yaml` runs only the infrastructure, and the three apps run on the host (ADR-0006).
In the containerized stacks (`compose.prod.yaml`, the Hetzner boxes) Caddy gives each app its own
HTTPS origin, and `tms-api` and the frontend reach `auth-api` through that public origin
(`Auth:Authority` / `AuthSystem:Authority`), so the token `iss` matches on every leg. The full setup
is in `CLAUDE.md` → "Commands" and `docs/deployment/runbook.md`.

- `tms-api` domain routes are under **`/api/v1/...`**. Its root `GET /` is the discovery document.
- `auth-api` routes live at the root: **`connect/*`** (OpenIddict) and **`auth/*`** (our own).
- Health (both APIs): `GET /health/live` and `GET /health/ready` are anonymous and run no checks. The
  full `GET /health` runs the checks only for a caller that sends the health check key in
  `X-LOTRO-Health-Key`; anyone else gets 404 (ADR-0058). When no key is set (allowed only in
  Development and Testing), it is open.
- OpenAPI/Scalar (`tms-api`, Development only, anonymous): `GET /openapi` and the Scalar UI.

---

## 2. Authentication & authorization model

### 2.1 Token issuance — `auth-api` (OpenIddict)

`auth-api` is a self-hosted **OpenIddict** server backed by **ASP.NET Core Identity**. It signs **JWT
access tokens** without encryption, so resource servers validate them as plain signed JWTs. The
public signing key is published through JWKS.

| Grant | Used by | Enabled |
|---|---|---|
| `authorization_code` + **PKCE** | the Blazor frontend (interactive login) | always (`RequireProofKeyForCodeExchange`) |
| `refresh_token` | the frontend (silent renewal) | always; **reference, rolling** tokens (stored in the database, revocable) |
| `client_credentials` | service to service (`lotrokoniecdev-api`) | always |
| `password` | integration and E2E tests only | **Testing environment only** |

OAuth clients seeded at startup (`DatabaseSeederExtensions.cs`):

| `client_id` | Type | Grants | Notes |
|---|---|---|---|
| `lotrokoniecdev-web` | public | auth code + PKCE, refresh | the frontend; redirect and post-logout URIs from config |
| `lotrokoniecdev-api` | confidential | client credentials | service to service; secret from config; may call introspection |
| `lotrokoniecdev-test` | public | password, refresh | seeded only in `Testing` |

Each access token names the APIs it is for in `aud`. A user's token names both `lotrokoniecdev-api`
(`tms-api`) and `lotrokoniecdev-auth-api` (the bearer endpoints of `auth-api`), because the website
sends the same token to both. Sign-in, code exchange and every refresh set this list
(`UserTokenAudiences`). The service's client credentials token names `lotrokoniecdev-api` only
(#1023).

Token lifetimes (`OpenIddictSettings.cs`): access **5 min** (ADR-0049), refresh **9 h**. Every refresh
issues a new refresh token with the full 9 h. The 9 h is the frontend cookie's 8-hour idle timeout plus
a margin, because the token lives in that cookie (#1014). E-mail confirmation and password-reset tokens
live **24 h**. Development and Testing use throwaway signing keys. Every other environment must supply
an RSA-2048 signing key and a 256-bit encryption key in config, with room for one previous signing key
during rotation.

### 2.2 Authorization — `tms-api` (JwtBearer)

`tms-api` validates issuer, audience, lifetime (no clock skew) and signing key against the `auth-api`
discovery document at `{authority}/.well-known/openid-configuration` (`ApiDependencyInjection.cs`).
`MapInboundClaims` is **off**, so claims keep their OpenIddict names (`sub`, `name`, `email`, `role`).
`NameClaimType=name`, `RoleClaimType=role`.

**Endpoints are authorized by default.** A fallback policy requires an authenticated user, so an
endpoint without its own metadata returns **401** to an anonymous caller. Public endpoints opt out with
`AllowAnonymous`.

| Policy | Rule | Applied to |
|---|---|---|
| (fallback) | authenticated user | every endpoint without its own metadata |
| `RequireAuthenticatedUser` | authenticated user, any role | `GET /api/v1/translators/me/data-export` |
| `RequireTranslatorRole` | role `Admin` **or** `Translator` | translation detail and stats, game-version reads, translation upsert |
| `RequireAdminRole` | role `Admin` | approve, bulk approve, register and delete a game version, import |
| `ApiScope` / `RequireServiceScope` | `scope` contains `api` / `service` | defined for service paths, used by no endpoint today |

Anonymous by explicit opt-out: the discovery document `GET /` (ADR-0040), `GET /api/v1/translation-files/{lang}`,
`GET /api/v1/progress` and the **read-only translations list** `GET /api/v1/translations` (#309,
the data is public game text). Every state change needs a login. On every authenticated request a
middleware creates the caller's TMS `Translator` profile if it is missing (ADR-0004, amended
2026-06-24); write handlers also do it before they stamp the author.

**Roles** (`AuthConstants.Roles`): `Admin` (reviewer — approves, imports, registers versions) and
`Translator` (edits Polish). A self-registered user gets **`Translator`**; the seeded admin gets
`Admin`. **Scopes** (`AuthConstants.Scopes`): `api`, `service` (plus the OIDC `openid email profile
roles offline_access`).

### 2.3 Request headers the client should send

```
Authorization: Bearer <access_token>      # every tms-api endpoint except the anonymous ones
Accept: application/json                   # default — responses without HATEOAS links
Accept: application/vnd.dev-lotrokoniecdev.hateoas.json   # opt into HATEOAS links (§4)
Content-Type: application/json             # JSON request bodies
Content-Type: multipart/form-data          # the import upload only
```

### 2.4 Rate limiting

Both APIs turn the limiter off in Development and Testing. Elsewhere:

| API | Policy | Limit | Applies to |
|---|---|---|---|
| `tms-api` | `fixed-by-ip` | 100 / min | every endpoint. The limiter runs before authentication, so a call refused with 401 or 403 also spends the bucket (#829) |
| `auth-api` | `fixed-by-ip` | 20 / min | the `auth/*` group by default |
| `auth-api` | `auth-endpoint-limit` | 10 / min | `connect/*`, `auth/confirm-email`, `auth/reset-password`, `auth/account/cancel-deletion`, `GET auth/account/data-export`, and the four link-landing pages (`ConfirmEmail`, `ConfirmEmailChange`, `RevertEmailChange`, `CancelDeletion`) |
| `auth-api` | `register-limit` | 10 / min | `auth/register`, keyed on the connection's own address |
| `auth-api` | `forgot-password-limit` | 3 / 15 min | `auth/forgot-password` and its page, one shared budget |
| `auth-api` | `resend-confirmation-limit` | 3 / 15 min | `auth/resend-email-confirmation` and its page |
| `auth-api` | `change-email-limit` | 3 / h | `auth/account/change-email` |
| `auth-api` | `auth-page-limit` | 10 / 15 min, **POSTs only**, per page and client | every other account page (the default for the whole Razor group, #692) |
| `auth-api` | none | — | `auth/change-password`, `auth/account/delete` and `POST auth/account/data-export` (braked per account instead, ADR-0053); the health probes; the OpenIddict discovery and JWKS documents |

**Which client a bucket belongs to (ADR-0054).** Every frontend call leaves from one container. So for
`tms-api`'s policy and the three `auth-api` policies the frontend reaches (`fixed-by-ip`,
`auth-endpoint-limit`, `change-email-limit`), each API's `RateLimitPartitionKeyResolver` keys on the
visitor's address that the frontend sends in `X-LOTRO-Client-Address`, but only next to the
environment's key in `X-LOTRO-Frontend-Key`. Without a valid key and a parseable address, the call is
metered on the connection's own address. The key only picks the bucket: it opens no endpoint and skips
no budget. The page policies and `register-limit` always key on the connection's own address. An IPv6
address counts as its whole /64, and `::ffff:a.b.c.d` counts as the plain IPv4 address (#831).

**Budgets inside the handlers.** Mail sent to a typed address has a second budget, because an attacker
who rotates IP addresses gets a fresh IP budget every time (ADR-0055). It is keyed on the **inbox**
(`MailboxKey`: Identity's normalized address without a `+tag`, and without Gmail's dots and the
`googlemail.com` difference — ADR-0057). The mail still goes to the typed address.

| Flow | Budget | When it is spent | Refusal |
|---|---|---|---|
| Registration | 3 / 15 min per inbox | last check before the confirmation mail is queued | 429 `Auth.RegistrationMailboxThrottled`, no account |
| Resend confirmation | 3 / 15 min per inbox | before the send | silent (same neutral answer) |
| Password reset | 3 / 15 min **per confirmed account**, or per inbox across unconfirmed accounts (ADR-0057 §3) | before the send | silent (same neutral answer) |
| E-mail change | 3 / 15 min per inbox of the **new** address, whichever account asks | before the link is queued | 429 `Auth.EmailChangeRecipientThrottled` |
| Current-password confirmation | 10 / 15 min per account (change password, change e-mail, delete, GDPR export) | before the password check | 429 `Auth.PasswordConfirmationThrottled` |
| Account deletion schedule | 3 / h per account (#811) | last check before the schedule is saved | 429 `Auth.DeletionScheduleThrottled` |

A silent refusal keeps the neutral "if the account exists" answer, so it cannot reveal a recent
request. These budgets live in process memory, so two running containers would mean two budgets — the
same trade-off the IP policies make.

Every 429 from a limiter policy carries `Retry-After` (the window length, so an upper bound — #892),
and a browser gets a Polish page instead of the bare status text. The 429s from the handler budgets
carry no `Retry-After`; their window is fixed (one hour for the deletion schedule, 15 minutes for the
rest).

---

## 3. Strongly-typed IDs and enums

### 3.1 IDs — serialize as bare GUID strings

`TranslationId`, `GameVersionId`, `TranslatorId`, `PrecomputedTranslationFileId` and the shared
`IdentityId` are hand-written record structs over **GUID v7** (`Guid.CreateVersion7()`, time-ordered;
ADR-0010). In JSON they are a **bare GUID string** — `"0190f3a2-..."`, not `{ "value": "..." }`. Route
ids use the `:guid` constraint.

### 3.2 Enums — serialize as strings

`JsonStringEnumConverter` is registered, so enums are **strings** on the wire.

- **`TranslationStatus`**: `Untranslated`, `Draft`, `Approved`, `NeedsReview` (plus `Unset`, never
  sent). See [DOMAIN.md §state machine](DOMAIN.md#maszyna-stanów-translation--cykl-aktualizacji-spec-0001).
- **`GameVersionStatus`**: `Unprocessed`, `Processed`, `Superseded` (plus `Unset`, never sent).

---

## 4. Content negotiation: HATEOAS opt-in

Hypermedia links are **opt-in** (`Hateoas.Abstractions/MediaTypes.cs`, spec 0002):

- `Accept: application/json` (default) → the resource **without** a `links` array. Empty link
  collections are left out of plain JSON.
- `Accept: application/vnd.dev-lotrokoniecdev.hateoas.json` → the same resource **with** a `links`
  array of `{ "href", "rel", "method" }`.

A link appears only when the caller may follow it (ADR-0040): the link factory replays the target
endpoint's own policy, and the slice adds the state rules (a soft-removed translation gets only
`self`; `approve` only on a `Draft`/`NeedsReview` row). Pagination links appear only when they lead to
another page, so a boundary rel is left out on the page it points at (#545). Treat the link set as
"what can I do next", and never hard-code a URL you can read from a rel (ADR-0041).

---

## 5. Error contract (`ProblemDetails`)

Business failures return RFC 7807 **`ProblemDetails`** (`ErrorExtensions.cs`, the same mapping in
both APIs). The HTTP status comes from the error's type, and the machine-readable code is in the
**`errorCode`** extension member:

```json
{
  "status": 422,
  "type": "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/422",
  "title": "Data Conflict",
  "detail": "A translation cannot be approved without Polish content.",
  "errorCode": "TranslationEntity.CannotApproveWithoutTranslation"
}
```

| Error type | HTTP | Title |
|---|---|---|
| `Validation` | **400** | Validation Error |
| `NotFound` | **404** | Not Found |
| `Forbidden` | **403** | Forbidden |
| `DataConflict` | **422** | Data Conflict |
| `TooManyRequests` | **429** | Too Many Requests |
| (anything else) | **500** | Internal Server Error |

Exceptions are caught by each API's `ExceptionHandlers/` and also returned as `ProblemDetails`. One of
them matters to clients: on `tms-api`, a write built on a row that changed in the meantime (for
example an import changed it) fails the concurrency check and returns **409**. Branch on `errorCode`,
not on the `detail` text. The rules behind each `errorCode` are in [INVARIANTS.md](INVARIANTS.md).

---

## 6. Pagination

Paginated lists return a `PaginationResponse<T>`:

```json
{
  "items": [ ... ],
  "page": 1,
  "pageSize": 50,
  "totalCount": 1234,
  "totalPages": 25,
  "hasNextPage": true,
  "hasPreviousPage": false,
  "links": [ ... ]        // only with the HATEOAS media type
}
```

`page` defaults to `1` (at least 1); `pageSize` defaults to `50` and is **clamped to 1–100** on the
server. Unpaged collections (game versions — there are few) return a `CollectionResponse<T>`
(`{ items, links }`) instead.

Both list endpoints take an optional multi-field **`?sort=`** (`SortParser.cs`,
`IQueryableExtensions.ApplyMultipleSorting`): comma-separated keys, each with an optional `:asc`/`:desc`
(default ascending), e.g. `?sort=status:desc,fileId`. Keys — translations: `fileId`, `gossipId`,
`status`, `submittedAt` (orders by `UpdatedAt`); game versions: `version`, `detectedAt`, `status`.
`status` and `version` sort by the stored **text**, not by meaning. An unknown key falls back to the
slice's first default key instead of failing, and a unique key is always added at the end, so the
order is total. Without `sort`, translations order by `(FileId, GossipId)` and game versions by
`DetectedAt` descending.

---

## 7. AuthSystem endpoints (`auth-api`)

### 7.1 OpenIddict protocol endpoints

| Method | Route | Auth | Purpose |
|---|---|---|---|
| `GET` `POST` | `connect/authorize` | anonymous (cookie challenge) | authorization-code flow entry; sends a caller without a session to the login page |
| `POST` | `connect/token` | anonymous (client-authenticated) | token issuance (auth code / refresh / client credentials; password in Testing) |
| `GET` `POST` | `connect/userinfo` | bearer token | OIDC userinfo — `sub`, and `email`/`name`/`role` per granted scope |
| `GET` `POST` | `connect/logout` | anonymous | RP-initiated end-session for this device only: revokes the one authorization behind a valid `id_token_hint` and its tokens (#931) and clears this browser's sign-in cookie. Without a valid hint it revokes nothing |
| `POST` | `connect/revoke` | client-authenticated | token revocation |
| `POST` | `connect/introspect` | confidential client | token introspection (POST only — ADR-0061) |
| `GET` | `/.well-known/openid-configuration` | anonymous | OIDC discovery document |
| `GET` | `/.well-known/jwks` | anonymous | JSON Web Key Set (public signing key) |

A request that OpenIddict or a `connect/*` handler refuses gets the OAuth error shape, not
`ProblemDetails`: `error` and `error_description`. `connect/token` sends them as a JSON body (RFC 6749
§5.2). `connect/authorize` sends them back to the client's `redirect_uri` in the query string (RFC 6749
§4.1.2.1). Every refusal the token handler makes itself is **400** `invalid_grant` (#903): a code or
refresh token that is no longer valid, wrong credentials on the Testing-only password grant, and that
grant's `account_deletion_scheduled` answer (in `error_description`). OpenIddict's own checks can give
other codes, for example **401** `invalid_client` for a wrong client secret or **400**
`unsupported_grant_type`.

Every `auth-api` endpoint that takes a bearer token (`connect/userinfo` and the `auth/*` rows marked
"bearer token" below) takes only a token whose `aud` names `lotrokoniecdev-auth-api`. Any other token,
the service's client credentials token too, gets **401** with `error="invalid_token"` in
`WWW-Authenticate` (#1023). `connect/userinfo` answers the same way for an account that no longer
exists (#955). A 429 from the limiter and a 500 from an unhandled exception come from the shared
pipeline as `ProblemDetails`: the status code already tells a temporary failure from a final one
(ADR-0067 §8).

The sign-in UI is server-rendered Razor Pages: `/Account/Login`, `/Account/Register`,
`/Account/ConfirmEmail`, `/Account/ResendConfirmation`, `/Account/ForgotPassword`,
`/Account/ResetPassword`, `/Account/ConfirmEmailChange`, `/Account/RevertEmailChange`,
`/Account/CancelDeletion` (the landing page of the e-mailed cancel link — LEGAL-01) and
`/Account/PrivacyPolicy`.

### 7.2 Account & credential endpoints (`auth/*`)

| Method | Route | Auth | Body / result |
|---|---|---|---|
| `POST` | `auth/register` | anonymous | `RegisterRequest` → **202** with no body, whether or not the address already has an account (#860). A free address gets an account with the `Translator` role and a confirmation mail; a taken one gets a notice to its owner instead; a reserved one (#684) gets nothing. A taken username is **422** `Auth.UserAlreadyExistsByUsername`; a breached password is **400** `Auth.PasswordFoundInBreaches` (ADR-0066) |
| `POST` | `auth/confirm-email` | anonymous | `ConfirmEmailRequest { email, token }` |
| `POST` | `auth/resend-email-confirmation` | anonymous | `ResendEmailConfirmationRequest` — always succeeds (hides whether the account exists) |
| `POST` | `auth/forgot-password` | anonymous | `ForgotPasswordRequest` — always succeeds (hides whether the account exists) |
| `POST` | `auth/reset-password` | anonymous | `ResetPasswordRequest { email, token, newPassword }` |
| `POST` | `auth/change-password` | bearer token | `ChangePasswordRequest { currentPassword, newPassword }` |
| `POST` | `auth/account/delete` | bearer token | `DeleteAccountRequest { password }` — **schedules** the GDPR deletion (ADR-0031): 14-day grace window, account locked for the window, sessions and refresh tokens revoked, one-time cancel link e-mailed → **204** with `X-Deletion-Scheduled-At` / `X-Deletion-Finalizes-At` headers. The erasure runs in the finalizer after the window |
| `POST` | `auth/account/cancel-deletion` | anonymous (e-mailed one-time token) | `CancelAccountDeletionRequest { email, token }` → **200** `CancelAccountDeletionResponse { passwordResetToken }` — unlocks the account and forces a password reset (the old password may be the attacker's) |
| `POST` | `auth/account/change-email` | bearer token | `ChangeEmailRequest { newEmail, currentPassword }` — **starts** an e-mail change (ADR-0048). Nothing on the account changes yet: a warning goes to the old address, then a verification link (24 h) to the new one. The answer is the same for any new address (#860): if it already has an account, its owner gets a notice instead of the link, and a reserved address (#684) gets nothing. Confirming happens on `/Account/ConfirmEmailChange` (GET shows the form, POST applies). Then the old address gets a 14-day link to `/Account/RevertEmailChange`, which restores the address and clears the password |
| `GET` | `auth/account/data-export` | bearer token | `AccountDataExportResponse` — the **account representation** the "Moje konto" page renders, with the account's links. Not the GDPR export: it leaves out the contact details (`phoneNumber` is always `null` here) |
| `POST` | `auth/account/data-export` | bearer token | `DownloadAccountDataRequest { password }` → **200** `AccountDataExportResponse` without links — the **GDPR Art. 15 export**, contact details included, only behind the current password (#690, ADR-0052). Wrong password → **400** `Auth.InvalidCurrentPassword`, none sent → **400** `Auth.ExportPasswordRequired`. Every attempt that reaches the password check is written to the audit log |
| `GET` | `/` | anonymous | discovery document (links into the account flows) |

`RegisterRequest`: `{ username, email, password, acceptedPrivacyPolicy, acceptedTermsOfService }`.
Both flags **must be `true`**; each acceptance and its time are stored and appear in the data export.
There is no data-processing consent: the account runs on the contract, not on consent (ADR-0068,
#1073). The data export still carries `dataProcessingConsentGiven`/`dataProcessingConsentDate`, set only
on older accounts, until the columns are dropped (ADR-0023, #1130).

Password rules (`PasswordValidationRules.cs`, lengths in `PasswordConstants.cs`): **8–128** characters,
at least one digit, one lowercase letter, one uppercase letter and one special character, and not in a
known data breach. The e-mail is unique (case-insensitive, enforced by the unique `EmailIndex`), ≤ 250,
regex-checked (`EmailConstants`), and it **is the login** (ADR-0022). The username is a
**display-only name**: unique (case-insensitive), letters and digits only (`UsernameConstants`), ≤ 150;
it never signs anyone in. A confirmed e-mail is **required to sign in**; lockout is **5 failed attempts
/ 5 min**.

> **Answers that hide whether an address has an account wait for a minimum time** (ADR-0059).
> `auth/forgot-password`, `auth/reset-password`, `auth/confirm-email` and `auth/account/cancel-deletion`
> answer no sooner than **500 ms** after the lookup starts, `auth/register` after **1 s**, and
> `auth/resend-email-confirmation` after **3 s** (it sends the mail inside the request). The matching
> auth pages and every failed sign-in on `/Account/Login` that shows the general message wait the
> same. A malformed request is refused at once.

> **No registration saga.** Registering creates only the AuthSystem user (ADR-0002 §7, ADR-0004). The
> TMS `Translator` profile is created on the caller's first authenticated request to `tms-api` (§2.2).

---

## 8. TranslationSystem endpoints (`tms-api`)

All routes are under `/api/v1` unless noted. All need a bearer token except `GET /`,
`GET /translation-files/{lang}`, `GET /progress` and the read-only `GET /translations` list.

### 8.0 Discovery

| Method | Route | Auth | 200 |
|---|---|---|---|
| `GET` | `/` | **anonymous** (ADR-0040) | `DiscoveryResponse { name }` + links |

The discovery document lists every entry point that takes no id, filtered by what the caller may
reach: an anonymous caller gets `self`, `translation-file`, `progress` and `translations`; a logged-in
user also gets the entry points their role allows (§10.1). Clients resolve every path from it by rel
name (ADR-0041).

### 8.1 Game versions

| Method | Route | Policy | Result |
|---|---|---|---|
| `GET` | `/api/v1/game-versions` | `RequireTranslatorRole` | **200** `CollectionResponse<GameVersionResponse>`, newest first (optional `?sort=`, §6) |
| `GET` | `/api/v1/game-versions/{id:guid}` | `RequireTranslatorRole` | **200** `GameVersionResponse` · **404** |
| `POST` | `/api/v1/game-versions` | `RequireAdminRole` | **201** `GameVersionResponse` (`Location: /api/v1/game-versions/{id}`) · **400** · **422** (version already registered) |
| `DELETE` | `/api/v1/game-versions/{id:guid}` | `RequireAdminRole` | **204** · **400** · **404** · **422** — see below |
| `POST` | `/api/v1/game-versions/{id:guid}/import` | `RequireAdminRole` | **200** `ImportSummary` · **404** (unknown version) · **413** (upload too large) · **422** (parse / empty / duplicate / mass removal / superseded) — see §8.3 |

`GameVersionResponse`: `{ id, version, detectedAt, status }`. `RegisterGameVersionRequest`:
`{ version }` (dotted notation, e.g. `"48.0"`, ≤ 12 chars, one to three segments; canonicalized —
`48` = `48.0`).

**`DELETE /game-versions/{id}`** (#209) removes a version that was registered by mistake. A
`Processed` version is never deleted, because an import ran against it. An `Unprocessed` or
`Superseded` version with **no translation pointing at it** may be deleted, which frees its version
number (#624). Otherwise **422** (`GameVersionEntity.ProcessedCannotBeDeleted` /
`GameVersionEntity.CannotDeleteReferencedVersion`); an unknown id is **404**. The duplicate check on
`POST` ignores status: deleting the wrong row is the way back, not a second row with the same version.

### 8.2 Translations

| Method | Route | Policy | Result |
|---|---|---|---|
| `GET` | `/api/v1/translations` | **anonymous** (read-only browse, #309) | **200** `PaginationResponse<TranslationListItemResponse>` · **400** (unsupported lang) |
| `GET` | `/api/v1/translations/stats` | `RequireTranslatorRole` | **200** `TranslationStatsResponse` |
| `GET` | `/api/v1/translations/{id:guid}` | `RequireTranslatorRole` | **200** `TranslationDetailResponse` · **404** |
| `PUT` | `/api/v1/translations` | `RequireTranslatorRole` | **200** `TranslationDetailResponse` · **400** · **403** · **404** · **422** |
| `POST` | `/api/v1/translations/{id:guid}/approve` | `RequireAdminRole` | **204** · **400** · **403** · **404** · **422** |
| `POST` | `/api/v1/translations/approve` | `RequireAdminRole` | **200** `BulkApproveTranslationsResponse` · **400** · **403** — see below |

**`GET /translations`** query parameters:

| Param | Type | Default | Meaning |
|---|---|---|---|
| `lang` | string? | — | optional; only `polish` exists today (anything else → **400**) |
| `search` | string? | — | case-insensitive substring of the English source **or** the Polish (LIKE special characters escaped) |
| `status` | `TranslationStatus`? | — | filter; `NeedsReview` is the "needs retranslation" view |
| `page` | int | `1` | at least 1 |
| `pageSize` | int | `50` | clamped to 1–100 |
| `sort` | string? | — | multi-field sort, §6 (`fileId`, `gossipId`, `status`, `submittedAt`) |

Soft-removed rows are always left out. Default order `(FileId, GossipId)`. Anonymous callers get the
items without any links.

**`PUT /translations`** body `UpsertTranslationRequest`: `{ fileId, gossipId, translatedText }`.
Creates or replaces the Polish text of the row keyed by `(fileId, gossipId)`; the row must already
exist (it comes from an import). `translatedText` must not be empty and must be at most 32767
characters, the most the DAT can hold (ADR-0043). The translator is taken from the **token**, never
the body. Editing an `Approved` row moves it to `Draft`, which takes it out of the file until someone
approves it again. A soft-removed row → **422** (`TranslationEntity.CannotEditRemoved`); unknown
fragment → **404**. Placeholders (`<--DO_NOT_TOUCH!-->`) are stored as they are.

**`POST /translations/{id}/approve`** (reviewer only): a row with no Polish → **422**
(`CannotApproveWithoutTranslation`), a soft-removed row → **422** (`CannotApproveRemoved`), unknown id
→ **404**, success → **204**. It schedules a background rebuild of the file (PERF-04, ADR-0021); the
response does not wait for it, so a download can trail the commit for a moment.

**`POST /translations/approve`** — bulk approve (#322), body `BulkApproveTranslationsRequest`:
`{ ids: [guid, ...] }` (1–100 ids, the list's largest page; duplicates are removed). Every requested
row that can *still* be approved (a non-removed `Draft`/`NeedsReview` row) is approved and the rest are
skipped, so one out-of-date row never fails the batch, and there is no 404/422 for a single row.
Response `BulkApproveTranslationsResponse`: `{ requested, approved, skipped }` with
`approved + skipped == requested`. One background rebuild is scheduled, only when at least one row was
approved.

`TranslationDetailResponse` (the side-by-side editor):

```json
{
  "id": "0190...", "fileId": 620756992, "gossipId": 1002,
  "sourceText": "Welcome to Middle-earth, <--DO_NOT_TOUCH!-->!",
  "argsOrder": "1", "argsId": "1",
  "translatedText": "Witaj w Śródziemiu, <--DO_NOT_TOUCH!-->!",
  "previousSourceText": null,
  "submitter": { "id": "0190...", "displayName": "Aragorn" },
  "approver":  null,
  "status": "Draft",
  "createdAt": "2026-06-18T10:00:00+00:00",
  "updatedAt": "2026-06-19T08:30:00+00:00"
}
```

`TranslationListItemResponse` is the lighter `{ id, fileId, gossipId, sourceText, translatedText,
status, submitter, updatedAt }`. `previousSourceText` is the **old English** kept for review after a
game update invalidated the row; it is set from the invalidation until the row is approved again.

`TranslationStatsResponse` (mini dashboard): `{ total, translated, approved, remaining }` over the
active (non-removed) catalog. `translated` = rows with Polish (Draft + Approved + NeedsReview),
`remaining` = `total - approved`. It comes from a **30 s server-side cache** (`HybridCache`,
AUDIT-EF-04), so the numbers can lag a write by up to 30 s.

### 8.3 Import (`POST /game-versions/{id}/import`)

Upload of a fresh `exported.txt` for one version (spec 0001), **admin only**, `multipart/form-data`:

- form field `file` — the `||`-format export. The size limit is **256 MB** by default
  (`ImportUploadLimits.MaxUploadBytes`, configurable as `Import:MaxUploadBytes`); a larger file →
  **413**;
- query `allowMassRemoval` (default `false`) — turns off the mass-removal guard.

The handler reads the upload **twice** (spec 0006). The first pass validates the file and compares it
with the stored sources by `(FileId, GossipId)`, hash to hash, without writing anything, so the
mass-removal guard sees the full plan first. The second pass applies the five outcomes (added — with
binary `COPY` — / source-changed / removed / restored / unchanged) and sets the version to `Processed`
in **one atomic transaction**. In the same transaction every older version that is still `Unprocessed`
becomes `Superseded`; a later import against one of them fails with
`GameVersionEntity.SupersededCannotBeProcessed` (**422**). After the commit a background rebuild of the
file is scheduled (ADR-0021). Importing again into a `Processed` version is allowed and idempotent.
Response `ImportSummary` (`echoed` counts rows whose "source" was our own Polish from a patched game,
already included in `unchanged` — spec 0012; `warnings` report restored rows and superseded versions):

```json
{ "added": 12, "sourceChanged": 3, "invalidated": 2, "removed": 0, "unchanged": 4810, "echoed": 0, "warnings": [] }
```

Failure modes (all **422 `DataConflict`** unless noted):

| `errorCode` | When |
|---|---|
| `Import.ParseFailed` | the upload has lines that cannot be parsed, is not valid UTF-8, or has a `source_digest` that does not match its row (the cut-off file guard) |
| `Import.EmptyUpload` | no translatable rows (empty or comments only) |
| `Import.InvalidRow` | a row has an invalid `(FileId, GossipId)` or source |
| `Import.DuplicateFragmentKey` | two rows share one fragment key |
| `Import.MassRemovalBlocked` | the upload would remove > **20%** of active rows without `allowMassRemoval=true` |
| `GameVersionEntity.SupersededCannotBeProcessed` | the target version was superseded by a newer processed one |
| `GameVersionEntity.NotFound` | unknown version id (**404**) |

### 8.4 Translation-file distribution (`GET /translation-files/{lang}`)

| Method | Route | Auth | Result |
|---|---|---|---|
| `GET` | `/api/v1/translation-files/{lang}` | **anonymous** | **200** `text/plain; charset=utf-8` (the `||` file) · **304** · **404** |

The CLI and the player app download the ready-made Polish file here. A background worker rebuilds it
after every write that changes the distributed set (PERF-04, ADR-0021). It is never built per request,
so a download can trail a commit for a moment. A full download streams a copy from the API's own disk,
written once per content hash (ADR-0064). HTTP caching:

- the response carries a strong **`ETag`** = the content hash (hex SHA-256 of the UTF-8 body) and
  `Cache-Control: private, no-cache`;
- send **`If-None-Match: "<etag>"`** (or `*`) to get **304 Not Modified** when nothing changed. The
  304 decision reads only the hash, never the multi-MB content (PERF-01);
- the ETag is also the **integrity hash** (AUDIT-SEC-01): the patcher computes the SHA-256 of the
  downloaded body and refuses the file on a mismatch. The hash algorithm and the strong-ETag format
  are a contract between the two contexts;
- `lang` must be `polish` (anything else → **400**); no file built yet → **404**
  (`TranslationFiles.NotFound`).

The file holds the `Approved`, non-removed rows, sorted by `FileId` then `GossipId`, byte-compatible
with the patcher's writer (approved always `1`, CRLF line ends). `source_digest` (ADR-0047) is the
16-hex digest of the row's English source: the patcher checks the DAT against it before it overwrites
a fragment, so Polish approved for an older English never lands over a changed text. Format:
[README → Translation file format](../README.md#translation-file-format-the-inter-context-contract). Projection:
[DOMAIN.md §projection](DOMAIN.md#projekcja-precomputedtranslationfile).

### 8.5 Public progress (`GET /progress`)

| Method | Route | Auth | 200 |
|---|---|---|---|
| `GET` | `/api/v1/progress` | **anonymous** | `PublicProgressResponse` |

The landing page's public numbers (#309): `{ total, translated, approved, currentGameVersion }` over
the active catalog — the same grouping as `/translations/stats` — plus the newest **Processed** game
version (`null` until the first import). A separate public contract with totals only, from a **30 s
server-side cache** (`HybridCache`).

### 8.6 Contribution data export (`GET /translators/me/data-export`)

| Method | Route | Policy | 200 |
|---|---|---|---|
| `GET` | `/api/v1/translators/me/data-export` | `RequireAuthenticatedUser` | `TranslatorDataExportResponse` |

The TMS half of the GDPR Art. 15 export (LEGAL-07, ADR-0032): the caller's translator profile
(`null` if none exists) and ids and per-status counts of every row they submitted or approved, never
the texts. Soft-removed rows are included. No role is needed, because access to your own data must not
depend on your role. It asks for no password; the frontend calls it only after `auth-api` accepted the
password for the Art. 15 file (ADR-0052). Its rel `contribution-data-export` is also what the frontend
uses to detect a token that never reached the API — see the warning on `Rels.ContributionDataExport`.

---

## 9. Workflow primers

### 9.1 Translator edits a string (browser, interactive)

1. Frontend redirects to `connect/authorize` (auth code + PKCE); the user signs in at `/Account/Login`.
2. `connect/token` returns the tokens; the cookie session stores them (`SaveTokens`).
3. `GET /api/v1/translations?status=Untranslated` → pick a row → `GET /api/v1/translations/{id}`.
4. `PUT /api/v1/translations { fileId, gossipId, translatedText }` → the row becomes `Draft`.

### 9.2 Reviewer approves & the file ships

1. `GET /api/v1/translations?status=Draft` (or `NeedsReview`).
2. `POST /api/v1/translations/{id}/approve` (Admin) → **204** — or select up to 100 rows and send
   `POST /api/v1/translations/approve { ids }`. A background rebuild of the file follows.
3. The CLI `launch` flow does `GET /api/v1/translation-files/polish` with `If-None-Match` and patches
   only when the ETag changed (and checks the body against the ETag hash).

### 9.3 A game update lands

1. CLI `export` → `exported.txt`. Admin: `POST /api/v1/game-versions { version }` (registration stays
   manual — ADR-0030), then `POST /api/v1/game-versions/{id}/import` with the file.
2. The diff invalidates source-changed rows that had Polish (→ `NeedsReview`, old English kept) and
   soft-removes rows that are gone; the rebuilt file leaves out both.
3. Translators work the `NeedsReview` queue; approving puts rows back into the file.

---

## 10. HATEOAS rel catalogue

Sent only with `Accept: application/vnd.dev-lotrokoniecdev.hateoas.json`. **Rel names are a frozen
public contract** (ADR-0040, ADR-0041): adding one is cheap, renaming one breaks every client.

### 10.1 `tms-api` rels (`TranslationSystem.Contracts/Hateoas/Rels.cs`)

Entry points in the discovery document `GET /` (each shown only to a caller who may follow it):

| rel | Method | Target |
|---|---|---|
| `self` | GET | `/` |
| `translation-file` | GET | `/api/v1/translation-files/polish` |
| `progress` | GET | `/api/v1/progress` |
| `translations` | GET | `/api/v1/translations` |
| `upsert` | PUT | `/api/v1/translations` |
| `translation-stats` | GET | `/api/v1/translations/stats` |
| `bulk-approve` | POST | `/api/v1/translations/approve` |
| `game-versions` | GET | `/api/v1/game-versions` |
| `register` | POST | `/api/v1/game-versions` |
| `contribution-data-export` | GET | `/api/v1/translators/me/data-export` |

Rels on resources and collections:

| rel | Method | Target | Appears when |
|---|---|---|---|
| `self` | GET | the resource / list page | always — except on a translation for a caller who is not a translator (every link there needs a login) |
| `upsert` | PUT | `/api/v1/translations` | caller is `Translator`/`Admin`, translation not soft-removed |
| `approve` | POST | `/api/v1/translations/{id}/approve` | caller is `Admin` **and** status is Draft or NeedsReview, not removed |
| `bulk-approve` | POST | `/api/v1/translations/approve` | on the translations collection, caller is `Admin` |
| `register` | POST | `/api/v1/game-versions` | on the game-versions collection, caller is `Admin` |
| `delete` | DELETE | `/api/v1/game-versions/{id}` | caller is `Admin` **and** the version is not `Processed` (#624) |
| `import` | POST | `/api/v1/game-versions/{id}/import` | caller is `Admin` **and** the version is not `Superseded` |
| `previous-page` / `next-page` | GET | the list page | that neighbouring page exists |
| `first-page` / `last-page` | GET | the list page | the target page exists **and is not the current one** (#545); both stay on an out-of-range page, so the caller can get back |

### 10.2 `auth-api` rels (`AuthSystem.Contracts/Hateoas/Rels.cs`)

Discovery: `self`, `register` and `forgot-password` for an anonymous caller, `export-account-data` for
a logged-in one. Account representation: `self`, `download-account-data`, `change-password`,
`change-email`, `delete-account`, and `resend-email-confirmation` while the e-mail is unconfirmed.
`download-account-data` (POST `auth/account/data-export`) is the password-gated GDPR export and is not
in discovery (ADR-0052). While a deletion is scheduled the account representation emits only `self`,
`cancel-deletion` (POST `auth/account/cancel-deletion`) and `download-account-data`: every other action
is a dead end on a locked account, and a copy of your own data stays a right.

---

## 11. Quick conventions checklist

- **Base paths**: `tms-api` domain under `/api/v1`; `auth-api` under `connect/*` + `auth/*`.
- **Auth**: bearer token on every `tms-api` call except `GET /`, `GET /translation-files/{lang}`,
  `GET /progress`, the read-only `GET /translations` list, and health.
- **Roles**: `Translator` reads and upserts; `Admin` also approves (single and bulk), imports, and
  registers and deletes versions.
- **IDs**: bare GUID strings (GUID v7). **Enums**: strings.
- **Errors**: RFC 7807 `ProblemDetails` on `tms-api` and `auth/*`; branch on the `errorCode`
  extension, not `detail`. A `connect/*` refusal uses the OAuth `error` / `error_description` shape
  instead (§7.1).
- **Links**: opt in with the vendor `Accept`; they follow the caller's rights and the resource state —
  drive the UI off rels.
- **Pagination**: `pageSize` clamped to 1–100; fixed default order + optional multi-field
  `?sort=key:asc,key2:desc` (§6).
- **Distribution**: respect `ETag` / `If-None-Match` on the translation-file download; the ETag is
  also the SHA-256 integrity hash of the body.

---

## 12. Where to read more

- [DOMAIN.md](DOMAIN.md) — the aggregates, value objects, state machine and CQRS split behind these endpoints.
- [INVARIANTS.md](INVARIANTS.md) — every enforced rule, tagged 🟢 Domain / 🔵 Application, with its file.
- [auth-tutorial.md](auth-tutorial.md) — the sign-in setup end to end (JWT, OAuth2/OIDC, OpenIddict, JwtBearer, lazy provisioning).
- `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` — the update-lifecycle domain spec.
- `docs/specs/0002-hateoas-hypermedia-on-tms-endpoints.md` — the HATEOAS content-negotiation contract.
- `docs/adr/` — 0001 (no mediator), 0002 (TMS pivot), 0003 (version canonical form), 0004 (translator
  + lazy provisioning), 0007 (read projections are not aggregates), 0021 (background artifact rebuild),
  0040 (authorization-aware links, anonymous root), 0041 (no API gateway), 0067 (who checks what).
