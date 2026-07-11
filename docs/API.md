# LotroKoniecDev TMS — API reference for frontend & CLI integrators

> Generated from the **actual code** (`TranslationSystem.API/Features/**`, `AuthSystem.API/Features/**`,
> the JwtBearer/OpenIddict wiring), not from prose. **The code is the source of truth** — when a
> route here disagrees with `Features/**`, read the slice and fix this doc. Every route below was
> verified present in the source. Companion docs: [DOMAIN.md](DOMAIN.md) (the model behind these
> endpoints), [INVARIANTS.md](INVARIANTS.md) (the rules they enforce), [auth-tutorial.md](auth-tutorial.md)
> (the auth story end-to-end).

The TMS is **two backend services** sharing one OpenIddict issuer: `auth-api` (the authorization
server) mints tokens; `tms-api` (the resource server) serves the translation domain and validates
those tokens. A Blazor SSR frontend (the OIDC relying party) and the patcher CLI are the clients.

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

| Service | Role | Host URL (HTTPS) | In-network (containerized) |
|---|---|---|---|
| `auth-api` | OpenIddict authorization server + ASP.NET Identity user store | `https://localhost:5003` | `http://auth-api:8080` |
| `tms-api` | Translation domain resource server (JwtBearer) | `https://localhost:5002` | `http://tms-api:8080` |
| Frontend (Blazor SSR) | OIDC relying party — runs on the host | `https://localhost:7017` | — |

In dev the `compose.yaml` is **infra-only** (postgres + migrator + mailpit + aspire) and all three apps
run on the **host** (ADR-0006, amended by #190 / M6-14) — each via its `https` `launchSettings` profile,
served with the native ASP.NET Core dev cert. The `http://…:8080` in-network addresses above apply to the
containerized stacks (`compose.prod.yaml` / a real deployment), where tms-api → auth-api JWKS resolves via
the in-network host; in dev that back-channel uses the `Auth:Authority`→`Auth:Issuer` fallback to
`https://localhost:5003`. Because the same host ports serve every workflow, the `TranslationSystem` base
URL, the OIDC `Authority`, and the token `iss` never change between workflows.

- `tms-api` base path for the domain: **`/api/v1/...`**. Its root `GET /` is a discovery document.
- `auth-api` endpoints live at the root: **`connect/*`** (OpenIddict) and **`auth/*`** (custom).
- Health (`tms-api`, anonymous): `GET /health`, `/health/live`, `/health/ready`.
- OpenAPI/Scalar (`tms-api`, Development only, anonymous): `GET /openapi`, Scalar UI.

---

## 2. Authentication & authorization model

### 2.1 Token issuance — `auth-api` (OpenIddict)

`auth-api` is a self-hosted **OpenIddict** authorization server backed by **ASP.NET Core Identity**.
It signs **JWT access tokens** (encryption disabled, so resource servers validate them as plain
signed JWTs — `OpenIddictExtensions.cs:64`) and exposes the public signing key via JWKS.

| Grant | Used by | Enabled |
|---|---|---|
| `authorization_code` + **PKCE** | the Blazor frontend (interactive login) | always (`RequireProofKeyForCodeExchange`) |
| `refresh_token` | the frontend (silent renewal) | always; **reference, rolling** tokens (DB-stored, revocable) |
| `client_credentials` | service-to-service (`lotrokoniecdev-api`) | always |
| `password` | integration / E2E tests only | **Testing environment only** |

OAuth clients seeded at startup (`DatabaseSeederExtensions.cs:109`):

| `client_id` | Type | Grants | Notes |
|---|---|---|---|
| `lotrokoniecdev-web` | public | auth code + PKCE, refresh | the Blazor RP; redirect/post-logout URIs from config |
| `lotrokoniecdev-api` | confidential | client credentials | service-to-service; secret from config |
| `lotrokoniecdev-test` | public | password, refresh | seeded only in `Testing` |

Token lifetimes (`OpenIddictSettings.cs`): access **60 min**, refresh **14 days**. Email-confirmation
and password-reset tokens live **24 h** (`PersistenceDependencyInjection.cs:60`). Dev/Testing use
**ephemeral** signing keys; production supplies an RSA-2048 signing key (public half via JWKS) and a
≥256-bit symmetric encryption key via config, with one-previous-key rotation support.

### 2.2 Authorization — `tms-api` (JwtBearer)

`tms-api` validates bearer tokens against the `auth-api` issuer (`ApiDependencyInjection.cs:215`):
issuer, audience, lifetime and signing key are all validated; signing keys are pulled from
`{authority}/.well-known/openid-configuration`. `MapInboundClaims` is **off**, so claims arrive as
the raw OpenIddict types (`sub`, `name`, `email`, `role`). `NameClaimType=name`, `RoleClaimType=role`.

**Endpoints are authorized by default.** A fallback policy requires an authenticated user, so any
endpoint without explicit metadata returns **401** to anonymous callers; public endpoints opt out
with `AllowAnonymous` (`ApiDependencyInjection.cs:258-279`).

| Policy | Rule | Applied to |
|---|---|---|
| (fallback) | authenticated user | every endpoint without explicit metadata (e.g. `GET /`) |
| `RequireTranslatorRole` | role `Admin` **or** `Translator` | detail/stats reads, game-version reads + translation upsert |
| `RequireAdminRole` | role `Admin` | approve, bulk approve, register/delete game version, import |
| `RequireAuthenticatedUser` | authenticated user | available, currently unused by a slice |
| `ApiScope` / `RequireServiceScope` | `scope` contains `api` / `service` | available for service paths |

Anonymous by explicit opt-out: `GET /translation-files/{lang}`, `GET /api/v1/progress` and — since
#309, the data being public game texts — the **read-only translations list** `GET /api/v1/translations`.
Every state transition stays authenticated. On every authenticated request a middleware lazily and
idempotently provisions the caller's TMS `Translator` profile (ADR-0004, amended 2026-06-24 —
`TranslatorProvisioningMiddleware.cs`); write handlers additionally provision authoritatively before
stamping attribution.

**Roles** (`AuthConstants.Roles`): `Admin` (reviewer — approves, imports, registers versions) and
`Translator` (edits Polish). A self-registered user gets **`Translator`** (`RegisterUser.cs:133`);
the seeded admin gets `Admin`. **Scopes** (`AuthConstants.Scopes`): `api`, `service` (plus the OIDC
`openid email profile roles offline_access`).

### 2.3 Request headers the client should send

```
Authorization: Bearer <access_token>      # every tms-api endpoint except the anonymous ones
Accept: application/json                   # default — responses without HATEOAS links
Accept: application/vnd.dev-lotrokoniecdev.hateoas.json   # opt into HATEOAS links (§4)
Content-Type: application/json             # JSON request bodies
Content-Type: multipart/form-data          # the import upload only
```

### 2.4 Rate limiting

In non-dev/test, `tms-api` applies a **fixed-window 100 requests/minute per IP** policy across the
endpoint group; over-limit returns **429** (`Program.cs:187-199`). `auth-api` rate-limits per IP: the
OpenIddict `/connect/*` endpoints and the sensitive account endpoints (`auth/register`,
confirm/reset/change-password, delete/export) carry the `auth-endpoint-limit` policy (10/min),
forgot-password and resend-confirmation carry stricter 3/15 min policies, and the remaining API
endpoints fall under the generic 20/min policy (health probes and the OpenIddict discovery/JWKS
documents are deliberately unlimited).

---

## 3. Strongly-typed IDs and enums

### 3.1 IDs — serialize as bare GUID strings

`TranslationId`, `GameVersionId`, `TranslatorId`, `PrecomputedTranslationFileId` and the
cross-context `IdentityId` are `[StronglyTypedId]` wrappers over **GUID v7** (`Guid.CreateVersion7()`,
time-ordered). They serialize to/from JSON as a **bare GUID string** — `"0190f3a2-..."`, not
`{ "value": "..." }`. Route ids use the `:guid` constraint.

### 3.2 Enums — serialize as strings

`JsonStringEnumConverter` is registered, so enums are **strings** on the wire.

- **`TranslationStatus`**: `Untranslated`, `Draft`, `Approved`, `NeedsReview` (plus `Unset`, never
  emitted). See [DOMAIN.md §state machine](DOMAIN.md#maszyna-stanów-translation--cykl-aktualizacji-spec-0001).
- **`GameVersionStatus`**: `Unprocessed`, `Processed`, `Superseded` (plus `Unset`, never emitted).

---

## 4. Content negotiation: HATEOAS opt-in

Hypermedia links are **opt-in** (the shared `MediaTypes` convention, spec 0002 — Implemented):

- `Accept: application/json` (default) → the resource **without** a `links` array. Empty link
  collections are stripped from plain JSON entirely.
- `Accept: application/vnd.dev-lotrokoniecdev.hateoas.json` → the same resource **with** a `links`
  array of `{ "href", "rel", "method" }`.

Links are **state- and role-aware**: a soft-removed translation advertises only `self`; `approve`
appears only for an `Admin` on a `Draft`/`NeedsReview` row; `register` (game version) only for an
`Admin`. Pagination links (`first-page`/`previous-page`/`next-page`/`last-page`) appear only when the
target page exists. Treat the link set as the authoritative "what can I do next" — don't hard-code
URLs you can read from a rel.

---

## 5. Error contract (`ProblemDetails`)

Business failures return RFC 7807 **`ProblemDetails`** (`ErrorExtensions.cs`). The HTTP status is
derived from the domain error's type, and the machine-readable code rides in the **`errorCode`**
extension member:

```json
{
  "status": 422,
  "type": "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/422",
  "title": "Data Conflict",
  "detail": "A translation cannot be approved without Polish content.",
  "errorCode": "TranslationEntity.CannotApproveWithoutTranslation"
}
```

| Domain error type | HTTP | Title |
|---|---|---|
| `Validation` | **400** | Validation Error |
| `NotFound` | **404** | Not Found |
| `Forbidden` | **403** | Forbidden |
| `DataConflict` | **422** | Data Conflict |
| (anything else) | **500** | Internal Server Error |

Unhandled exceptions are caught by the API's `ExceptionHandlers/` chain and also returned as
`ProblemDetails` (a safety net, not a control-flow mechanism). Branch on `errorCode`, not on the
`detail` string. The full enforced-rule catalogue with `errorCode`s lives in
[INVARIANTS.md](INVARIANTS.md).

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

`page` defaults to `1` (clamped to ≥ 1); `pageSize` defaults to `50` and is **clamped to 1–100**
server-side. Unpaged collections (game versions — few rows ever exist) return a `CollectionResponse<T>`
(`{ items, links }`) instead.

Both list endpoints accept an optional multi-field **`?sort=`** parameter
(`SortParser.cs` / `IQueryableExtensions.ApplyMultipleSorting`): comma-separated keys, each optionally
suffixed `:asc`/`:desc` (default ascending), e.g. `?sort=status:desc,fileId`. Supported keys —
translations: `fileId`, `gossipId`, `status`, `submittedAt` (orders by `UpdatedAt`); game versions:
`version`, `detectedAt`, `status`. `status`/`version` sort by the stored **string**, not semantically;
an unknown key degrades to the slice's primary default instead of failing, and a unique tiebreaker is
always appended so pagination order is total. Without `sort`, translations order by
`(FileId, GossipId)` and game versions by `DetectedAt` desc.

---

## 7. AuthSystem endpoints (`auth-api`)

### 7.1 OpenIddict protocol endpoints

| Method | Route | Auth | Purpose |
|---|---|---|---|
| `GET` `POST` | `connect/authorize` | anonymous (cookie challenge) | authorization-code flow entry; challenges the Identity login cookie |
| `POST` | `connect/token` | anonymous (client-authenticated) | token issuance (auth code / refresh / client credentials; password in Testing) |
| `GET` `POST` | `connect/userinfo` | bearer token | OIDC userinfo — `sub`, and `email`/`name`/`role` per granted scope |
| `GET` `POST` | `connect/logout` | anonymous | RP-initiated end-session; revokes the user's reference tokens, clears the cookie |
| `POST` | `connect/revoke` | client-authenticated | token revocation |
| (OpenIddict) | `connect/introspect` | confidential client | token introspection |
| `GET` | `/.well-known/openid-configuration` | anonymous | OIDC discovery document |
| `GET` | `/.well-known/jwks` | anonymous | JSON Web Key Set (public signing key) |

The login/consent UI is server-rendered Razor Pages: `/Account/Login`, `/Account/ConfirmEmail`,
`/Account/ForgotPassword`, `/Account/ResetPassword`, `/Account/PrivacyPolicy`.

### 7.2 Account & credential endpoints (`auth/*`)

| Method | Route | Auth | Body / result |
|---|---|---|---|
| `POST` | `auth/register` | anonymous, rate-limited | `RegisterRequest` → **201** bare `IdentityId`; assigns `Translator`, sends confirmation email |
| `POST` | `auth/confirm-email` | anonymous | `ConfirmEmailRequest { email, token }` |
| `POST` | `auth/resend-email-confirmation` | anonymous | `ResendEmailConfirmationRequest` (anti-enumeration: always succeeds) |
| `POST` | `auth/forgot-password` | anonymous | `ForgotPasswordRequest` (anti-enumeration: always succeeds) |
| `POST` | `auth/reset-password` | anonymous | `ResetPasswordRequest { email, token, newPassword }` |
| `POST` | `auth/change-password` | bearer token | `ChangePasswordRequest { currentPassword, newPassword }` |
| `POST` | `auth/account/delete` | bearer token | `DeleteAccountRequest { password }` — GDPR erasure + permanent lockout |
| `GET` | `auth/account/data-export` | bearer token | `AccountDataExportResponse` — GDPR export |
| `GET` | `/` | anonymous | discovery document (links into the auth flows) |

`RegisterRequest`: `{ username, email, password, acceptedPrivacyPolicy, acceptedDataProcessingConsent }`.
Both consent flags **must be `true`**. Password rules (`PasswordValidationRules.cs`): **8–128** chars,
≥1 digit, ≥1 lowercase, ≥1 uppercase, ≥1 special. Email unique (case-insensitively, physical via the
unique `EmailIndex`), ≤ 250, regex-validated — **the e-mail is the login identifier** (ADR-0022).
Username is a **display-only handle**: unique (case-insensitively), `^[a-zA-Z0-9]+$` (letters + digits
only — `UsernameConstants`), ≤ 150; it never authenticates. Email confirmation is **required to sign
in**; lockout is **5 failed attempts / 5 min**.

> **No registration saga.** Registering creates only the AuthSystem user (ADR-0002 §7 / ADR-0004) —
> the KittySaver `RegisterUser → CreatePerson` saga is deliberately **not** lifted. The TMS
> `Translator` profile is provisioned lazily and idempotently on the caller's first authenticated
> request to `tms-api` (§2.2).

---

## 8. TranslationSystem endpoints (`tms-api`)

All routes are under `/api/v1` unless noted. All require a bearer token except
`GET /translation-files/{lang}`, `GET /progress` and the read-only `GET /translations` list.

### 8.0 Discovery

| Method | Route | Auth | 200 |
|---|---|---|---|
| `GET` | `/` | authenticated (fallback) | `DiscoveryResponse { name }` + links |

### 8.1 Game versions

| Method | Route | Policy | Result |
|---|---|---|---|
| `GET` | `/api/v1/game-versions` | `RequireTranslatorRole` | **200** `CollectionResponse<GameVersionResponse>`, newest-first (optional `?sort=`, §6) |
| `GET` | `/api/v1/game-versions/{id:guid}` | `RequireTranslatorRole` | **200** `GameVersionResponse` · **404** |
| `POST` | `/api/v1/game-versions` | `RequireAdminRole` | **201** `GameVersionResponse` (`Location: /api/v1/game-versions/{id}`) · **400** · **422** (version already registered) |
| `DELETE` | `/api/v1/game-versions/{id:guid}` | `RequireAdminRole` | **204** · **400** · **404** · **422** — see below |
| `POST` | `/api/v1/game-versions/{id:guid}/import` | `RequireAdminRole` | **200** `ImportSummary` · **404** (unknown version) · **413** (upload too large) · **422** (parse/empty/duplicate/mass-removal/superseded) — see §8.3 |

`GameVersionResponse`: `{ id, version, detectedAt, status }`. `RegisterGameVersionRequest`:
`{ version }` (dotted notation, e.g. `"48.0"`, ≤ 12 chars; canonicalized — `48` = `48.0`).

**`DELETE /game-versions/{id}`** (#209) removes a manually-registered version that was added by
mistake. The guard is strict: only an `Unprocessed` version with **no translation referencing it**
may be deleted — otherwise **422** (`GameVersionEntity.OnlyUnprocessedCanBeDeleted` /
`GameVersionEntity.CannotDeleteReferencedVersion`); an unknown id is **404**.

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
| `search` | string? | — | case-insensitive substring over English source **or** Polish (LIKE metachars escaped) |
| `status` | `TranslationStatus`? | — | filter; `NeedsReview` is the "needs re-translation" view |
| `page` | int | `1` | clamped ≥ 1 |
| `pageSize` | int | `50` | clamped 1–100 |
| `sort` | string? | — | multi-field sort, §6 (`fileId`, `gossipId`, `status`, `submittedAt`) |

Soft-removed rows are always excluded. Default order `(FileId, GossipId)`. The list is anonymous
(the data is public game text); anonymous callers get items without any HATEOAS action links.

**`PUT /translations`** body `UpsertTranslationRequest`: `{ fileId, gossipId, translatedText }`. Creates
or replaces the Polish content of the row keyed by `(fileId, gossipId)` — the row must already exist
(born from import). The submitting translator is taken from the **token**, never the body. Editing an
`Approved` row moves it to `Draft` (pulled from distribution until re-approved). A soft-removed row
→ **422** (`TranslationEntity.CannotEditRemoved`); unknown fragment → **404**. Placeholders
(`<--DO_NOT_TOUCH!-->`) are stored verbatim.

**`POST /translations/{id}/approve`** (reviewer only): a row with no Polish → **422**
(`CannotApproveWithoutTranslation`), a soft-removed row → **422** (`CannotApproveRemoved`), unknown
id → **404**, success → **204**. Schedules a debounced background rebuild of the distributed file
(PERF-04, ADR-0021) — the response does not wait for it, so a download may briefly trail the commit.

**`POST /translations/approve`** — bulk approve (#322), body `BulkApproveTranslationsRequest`:
`{ ids: [guid, ...] }` (1–100 distinct ids; the list's max page size). Best-effort: every requested
row that is *still* approvable (a non-removed `Draft`/`NeedsReview` row) is approved, the rest are
silently skipped — a single stale row never fails the batch, and no per-row 404/422 is returned.
Response `BulkApproveTranslationsResponse`: `{ requested, approved, skipped }` with
`approved + skipped == requested`. One debounced artifact rebuild is scheduled only when at least
one row was approved.

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

`TranslationListItemResponse` carries the lighter `{ id, fileId, gossipId, sourceText, translatedText,
status, submitter, updatedAt }`. `previousSourceText` is the **superseded English** kept for review
after a game update invalidated the row (non-null only while `NeedsReview`).

`TranslationStatsResponse` (mini-dashboard): `{ total, translated, approved, remaining }` over the
active (non-removed) catalog — `translated` = rows carrying Polish (Draft + Approved + NeedsReview),
`remaining` = `total - approved`. Served from a **30 s server-side cache** (`HybridCache`,
AUDIT-EF-04) — counters may lag a write by up to the TTL.

### 8.3 Import (`POST /game-versions/{id}/import`)

Version-bound upload of a fresh `exported.txt` (spec 0001), **admin-only**, `multipart/form-data`:

- form field `file` — the `||`-format export (size-capped: **256 MB** default via
  `ImportUploadLimits.MaxUploadBytes`, configurable `Import:MaxUploadBytes`; over the cap → **413**);
- query `allowMassRemoval` (default `false`) — override the safety guard.

The handler streams the upload in **two passes** (spec 0006): pass 1 validates and diffs the file
against the stored source state by `(FileId, GossipId)` hash-to-hash without writing anything (so the
mass-removal guard runs on the full plan first); pass 2 applies the five outcomes (added — binary
`COPY` — / source-changed / removed / restored / unchanged) and flips the version to `Processed` in
**one atomic transaction**. Processing the newest version also marks every older still-`Unprocessed`
version `Superseded` in the same transaction — a later import against one of them fails with
`GameVersionEntity.SupersededCannotBeProcessed` (**422**) instead of rewinding the catalog. After the
commit a debounced background rebuild of the distributed file is scheduled (ADR-0021). Re-upload to
an already `Processed` version is allowed and idempotent. Response `ImportSummary` (warnings report
restored rows and superseded versions):

```json
{ "added": 12, "sourceChanged": 3, "invalidated": 2, "removed": 0, "unchanged": 4810, "warnings": [] }
```

Failure modes (all **422 `DataConflict`** unless noted):

| `errorCode` | When |
|---|---|
| `Import.ParseFailed` | the upload has unparseable lines (truncated file guard) |
| `Import.EmptyUpload` | no translatable rows (empty / comments-only) |
| `Import.InvalidRow` | a row has an invalid `(FileId, GossipId)` or source |
| `Import.DuplicateFragmentKey` | two rows share one fragment key |
| `Import.MassRemovalBlocked` | the upload would remove > **20%** of active rows without `allowMassRemoval=true` |
| `GameVersionEntity.SupersededCannotBeProcessed` | the target version was superseded by a newer processed one |
| `GameVersionEntity.NotFound` | unknown version id (**404**) |

### 8.4 Translation-file distribution (`GET /translation-files/{lang}`)

| Method | Route | Auth | Result |
|---|---|---|---|
| `GET` | `/api/v1/translation-files/{lang}` | **anonymous** | **200** `text/plain` (the `||` file) · **304** · **404** |

The CLI/player downloads the pre-built Polish file here. Served from a stored artifact that a
**debounced background worker** regenerates after every write changing the distributed set (PERF-04,
ADR-0021) — never built per request, so a download may briefly trail a commit. Supports HTTP caching:

- the response carries a strong **`ETag`** = the content hash (hex SHA-256 of the UTF-8 body), plus
  `Cache-Control: private, no-cache`;
- send **`If-None-Match: "<etag>"`** (or `*`) to get **304 Not Modified** when unchanged — the 304
  decision is a hash-only lookup that never reads the multi-MB content (PERF-01);
- the ETag doubles as the **integrity hash** (AUDIT-SEC-01): the patcher recomputes the SHA-256 of
  the downloaded body and rejects the file on mismatch — the hash algorithm and strong-ETag format
  are a cross-context contract;
- `lang` must be `polish` (anything else → **400**); no artifact built yet → **404**
  (`TranslationFiles.NotFound`).

The file holds **Approved + non-invalidated + non-removed** rows, sorted by `FileId` then `GossipId`,
byte-compatible with the patcher's writer (the `args_order||args_id||approved` columns, approved always
`1`, CRLF terminators). See the [translation file contract](../CLAUDE.md) and
[DOMAIN.md §projection](DOMAIN.md#projekcja-precomputedtranslationfile).

### 8.5 Public progress (`GET /progress`)

| Method | Route | Auth | 200 |
|---|---|---|---|
| `GET` | `/api/v1/progress` | **anonymous** | `PublicProgressResponse` |

The landing page's public snapshot (#309): `{ total, translated, approved, currentGameVersion }` over
the active catalog — the same bucketing as `/translations/stats` — plus the newest **Processed** game
version's dotted notation (`null` until a first import completes). Deliberately a separate frozen
public contract, aggregate counters only; served from a **30 s server-side cache** (`HybridCache`).

---

## 9. Workflow primers

### 9.1 Translator edits a string (browser, interactive)

1. Frontend redirects to `connect/authorize` (auth code + PKCE); user signs in at `/Account/Login`.
2. `connect/token` returns the access token; the cookie session stores it (`SaveTokens`).
3. `GET /api/v1/translations?status=Untranslated` → pick a row → `GET /api/v1/translations/{id}`.
4. `PUT /api/v1/translations { fileId, gossipId, translatedText }` → row becomes `Draft`. (The
   caller's `Translator` profile was already provisioned lazily on their first authenticated request
   — ADR-0004 as amended; the write handler re-verifies it before stamping attribution.)

### 9.2 Reviewer approves & the file ships

1. `GET /api/v1/translations?status=Draft` (or `NeedsReview`).
2. `POST /api/v1/translations/{id}/approve` (Admin) → **204** — or select up to 100 rows and
   `POST /api/v1/translations/approve { ids }` in one action; a debounced background rebuild of the
   distributed file follows.
3. The CLI `launch` flow does `GET /api/v1/translation-files/polish` with `If-None-Match` → patches
   only when the ETag changed (and verifies the body against the ETag hash).

### 9.3 A game update lands

1. CLI `export` → `exported.txt`. Admin: `POST /api/v1/game-versions { version }` (manual — the forum
   watcher, M2-18/#85, is not implemented yet), then `POST /api/v1/game-versions/{id}/import` with
   the file.
2. The diff invalidates source-changed rows that carried Polish (→ `NeedsReview`, superseded English
   frozen) and soft-removes vanished rows; the rebuilt file excludes both.
3. Translators work the `NeedsReview` queue; approve re-admits rows to the file.

---

## 10. HATEOAS rel catalogue

Sent only with `Accept: application/vnd.dev-lotrokoniecdev.hateoas.json`.

### 10.1 `tms-api` rels (`TranslationSystem.Contracts/Hateoas/Rels.cs`)

| rel | Method | Target | Appears when |
|---|---|---|---|
| `self` | GET | the resource / list page | always — except on a translation for an **anonymous** caller (every advertised transition, incl. the detail `self`, requires auth) |
| `upsert` | PUT | `/api/v1/translations` | caller is `Translator`/`Admin`, translation not soft-removed |
| `approve` | POST | `/api/v1/translations/{id}/approve` | caller is `Admin` **and** status ∈ {Draft, NeedsReview}, not removed |
| `bulk-approve` | POST | `/api/v1/translations/approve` | on the translations collection, caller is `Admin` |
| `register` | POST | `/api/v1/game-versions` | on the game-versions collection, caller is `Admin` |
| `delete` | DELETE | `/api/v1/game-versions/{id}` | caller is `Admin` **and** the version is `Unprocessed` |
| `first-page` / `previous-page` / `next-page` / `last-page` | GET | the list page | the target page exists |

### 10.2 `auth-api` rels (`AuthSystem.Contracts/Hateoas/Rels.cs`)

`self`, `register`, `forgot-password`, `export-account-data` (discovery); `change-password`,
`delete-account`, `resend-email-confirmation` (account aggregate).

---

## 11. Quick conventions checklist

- **Base paths**: `tms-api` domain under `/api/v1`; `auth-api` under `connect/*` + `auth/*`.
- **Auth**: bearer token on every `tms-api` call except `GET /translation-files/{lang}`,
  `GET /progress`, the read-only `GET /translations` list, and health.
- **Roles**: `Translator` reads + upserts; `Admin` also approves (single + bulk) / imports /
  registers & deletes versions.
- **IDs**: bare GUID strings (GUID v7). **Enums**: strings.
- **Errors**: RFC 7807 `ProblemDetails`; branch on the `errorCode` extension, not `detail`.
- **Links**: opt in with the vendor `Accept`; they're state/role-aware — drive the UI off rels.
- **Pagination**: `pageSize` clamped 1–100; deterministic default order + optional multi-field
  `?sort=key:asc,key2:desc` (§6).
- **Distribution**: respect `ETag` / `If-None-Match` on the translation-file download; the ETag is
  also the SHA-256 integrity hash of the body.

---

## 12. Where to read more

- [DOMAIN.md](DOMAIN.md) — the aggregates, value objects, state machine and CQRS split behind these endpoints.
- [INVARIANTS.md](INVARIANTS.md) — every enforced rule, tagged 🟢 Domain / 🔵 Application with a `file:line` anchor.
- [auth-tutorial.md](auth-tutorial.md) — the auth story end-to-end (JWT, OAuth2/OIDC, OpenIddict, JwtBearer, lazy provisioning).
- `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` — the update-lifecycle domain spec.
- `docs/specs/0002-hateoas-hypermedia-on-tms-endpoints.md` — the HATEOAS content-negotiation contract.
- `docs/adr/` — 0001 (no mediator), 0002 (TMS pivot), 0003 (version canonical form), 0004 (translator
  + lazy provisioning), 0006 (dev stack: infra-only compose + host Kestrels), 0007 (read projections
  are not aggregates), 0021 (debounced background artifact rebuild).
