# Spec 0002: HATEOAS hypermedia on TMS resource endpoints

- **Status:** Implemented
- **Date:** 2026-06-16
- **Author:** Artur Koniec
- **Ticket:** #153 (M2-25)
- **Related:** ADR-0001 (no mediator / slim handlers), ADR-0002 (TMS pivot, CQRS read/write split),
  spec 0001 (update lifecycle); mirror refs: TheKittySaver `CatAggregateLinkFactory` /
  `PaginationLinkFactory` / `GetCats`, and the in-repo `AuthSystem` `AccountAggregateLinkFactory`.

## Business context

The HATEOAS library (`src/Utilities/LotroKoniecDev.Hateoas`) is already lifted 1:1 from TheKittySaver
and `AddHateoasInfrastructure()` is wired, but the TMS only emits links from the Discovery endpoint.
TheKittySaver — the architectural reference — emits hypermedia from every resource/collection read,
which is a showcase-grade (Richardson Maturity Level 3) REST capability. This ticket closes that gap
for the recruiter-facing goal, resolving the open question parked in #151 ("does HATEOAS apply to the
TMS?") with **yes**.

## Goal

Every TMS resource and collection **read** carries `links` (self + the actions the caller is
authorized for + pagination/collection navigation) under the HATEOAS vendor media type, while plain
`application/json` responses stay byte-for-byte unchanged.

## In scope

- Per-aggregate link factories mirroring KittySaver, de-mediatorized:
  - `TranslationAggregateLinkFactory` — per translation: `self`, `upsert`, `approve` (role-aware).
  - `GameVersionAggregateLinkFactory` — per version: `self`; per collection: `self` + `register`.
  - `PaginationLinkFactory` — `self`/`first-page`/`previous-page`/`next-page`/`last-page`,
    preserving the active query string.
- Links emitted from the **read** endpoints: `ListTranslations` (per-item + pagination),
  `GetTranslation`, `ListGameVersions` (per-item + collection), and a **new** `GetGameVersion`
  (`GET /api/v1/game-versions/{id}`) so a game version has a real `self` target.
- Contracts DTOs implement `ILinksResponse` (carry a settable `Links`); a new
  `CollectionResponse<T>` envelope for the unpaged game-version collection.
- Integration tests asserting rels, role-awareness, pagination filter preservation, and that plain
  JSON is unaffected.

## Out of scope

- **Command endpoints emit no links** — `UpsertTranslation`, `ApproveTranslation`,
  `RegisterGameVersion` are unchanged. This mirrors TheKittySaver exactly: **no** KittySaver command
  (POST/PUT/DELETE, incl. `CreateCat`/`UpdateCat`/`UpsertCatThumbnail`) returns hypermedia; the
  client mutates then re-GETs to discover the next transitions. `ApproveTranslation` therefore stays
  `204 No Content`.
- Multi-language: the catalog is Polish-only today.
- No new domain/persistence/read-model changes — pure API + Contracts.

## Business rules & edge cases

- **Vendor opt-in (already built):** links are attached only when the client sends
  `Accept: application/vnd.dev-lotrokoniecdev.hateoas.json`; otherwise the `links` key is suppressed
  entirely by `HateoasJsonTypeInfoModifiers.SuppressEmptyLinks`.
- **Translation rels (role + state aware):**
  - `self` → `GET /api/v1/translations/{id}` — always.
  - On a soft-removed row (`RemovedInVersion != null`) → **self only** (a removed row is excluded
    from translation work; `upsert`/`approve` would be dead transitions). `GetTranslation` projects
    the removal flag to drive this; `ListTranslations` already excludes removed rows, so list items
    are never removed.
  - `upsert` → `PUT /api/v1/translations` — for any non-removed row (the caller reaching a
    translation read is already ≥ Translator).
  - `approve` → `POST /api/v1/translations/{id}/approve` — only when the caller is **Admin** AND the
    row has Polish awaiting review (`Status` is `Draft` or `NeedsReview`). Hidden for `Untranslated`
    (nothing to approve) and `Approved` (idempotent dead-ish transition) — "don't advertise dead
    transitions".
- **GameVersion rels:**
  - per item: `self` → `GET /api/v1/game-versions/{id}`.
  - collection: `self` → `GET /api/v1/game-versions`; `register` → `POST /api/v1/game-versions`
    only when the caller is **Admin**.
- **Pagination:** `self`/`first`/`last` always when there is ≥1 page; `previous` when `Page > 1`;
  `next` when `Page < TotalPages`; every link preserves the active `lang`/`search`/`status` filters
  and `page`/`pageSize`.
- **Role source:** the endpoint reads the caller's `ClaimsPrincipal` (`IsInRole(Admin)`), consistent
  with `CurrentUserAccessor`.

## Contract

- **New endpoint:** `GET /api/v1/game-versions/{id:guid}` →
  `IQueryHandler<GetGameVersion.Query, Result<GameVersionResponse>>`; `200` + `GameVersionResponse`
  (with `self`), `404` via ProblemDetails for an unknown id. Authorized `RequireTranslatorRole`
  (mirrors `ListGameVersions`).
- **Changed read shapes (links via the JsonTypeInfo modifier — DTO bodies stay clean):**
  - `TranslationListItemResponse`, `TranslationDetailResponse`, `GameVersionResponse` implement
    `ILinksResponse`.
  - `ListGameVersions` now returns `CollectionResponse<GameVersionResponse>` (envelope:
    `Items` + `Links`) **instead of a bare array** — a deliberate breaking change (pre-release, no
    users); the existing `ListGameVersionsTests` is rewritten to the envelope.
  - `GetTranslation`'s handler returns an internal `QueryResult(TranslationDetailResponse, bool
    IsRemoved)`; the Contracts DTO is unchanged.
- **New Contracts type:** `CollectionResponse<T> : ILinksResponse` in `Contracts/Common`.
- **New rels** in `Contracts/Hateoas/Rels.cs`: `upsert`, `approve`, `register`, `first-page`,
  `previous-page`, `next-page`, `last-page`.
- **DI:** register `ITranslationAggregateLinkFactory`, `IGameVersionAggregateLinkFactory`,
  `IPaginationLinkFactory` (transient, mirroring `IDiscoveryLinkFactory`) and the `GetGameVersion`
  handler; update the `GetTranslation` handler registration to the new `QueryResult`.

## Acceptance criteria

- [ ] Under the vendor media type, `GET /api/v1/translations` returns each item with `self` +
      `upsert` (+ `approve` for admins on Draft/NeedsReview) and the envelope with
      `self`/`first`/`last` (+ `previous`/`next` where applicable).
- [ ] Under the vendor media type, `GET /api/v1/translations/{id}` returns `self` (+ `upsert`/
      `approve` per the rules above); a soft-removed row returns `self` only.
- [ ] A **Translator** (non-admin) never sees `approve`; an **Admin** sees `approve` only on
      Draft/NeedsReview rows.
- [ ] Pagination links preserve the active `search`/`status` filters and the page bounds.
- [ ] Under the vendor media type, `GET /api/v1/game-versions` returns each item with `self` and the
      envelope with `self` (+ `register` for admins); `GET /api/v1/game-versions/{id}` returns `self`
      and `404` for an unknown id.
- [ ] A plain `application/json` request to every touched endpoint carries **no** `links` key and
      deserializes exactly as before.
- [ ] Green build, zero warnings; only `ListGameVersionsTests` changes among existing tests (the
      sanctioned envelope change); new HATEOAS tests green.

## Open questions

- _(resolved 2026-06-16)_ **GameVersions link surface** → chose the **envelope + `register`** option:
  add `GetGameVersion` for a real per-item `self`, wrap the list in `CollectionResponse<T>` with a
  collection `self` + admin-only `register`. Accepts rewriting `ListGameVersionsTests`.
- _(resolved 2026-06-16)_ **Do commands return hypermedia?** → No. Verified empirically that **no**
  TheKittySaver command emits links; reads do, clients re-GET. Approve stays `204`.

## Assumptions

- `application/vnd.dev-lotrokoniecdev.hateoas.json` and the content negotiator / JsonTypeInfo modifier
  are correct as lifted (proven by the AuthSystem HATEOAS tests).
- Strongly-typed ids render into route values as their bare `Guid` (`ToString() => Value.ToString()`);
  links pass `id.Value` explicitly.
