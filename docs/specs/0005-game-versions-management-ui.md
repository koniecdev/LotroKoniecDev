# Spec 0005: Game versions management UI (Frontend) + guarded delete

- **Status:** Implemented (2026-06-28, #216 — `/game-versions` page: list/register/delete + guarded API DELETE)
- **Date:** 2026-06-28
- **Author:** koniecdev
- **Ticket:** #209 (FE — CRUD UI for managing GameVersion)
- **Related:** Spec 0001 (game-update lifecycle), ADR-0003 (LotroNotationVersion canonical form),
  #107/M2-19 (GameVersion endpoints), #158/M3-12 (HATEOAS-driven affordances), `Components/Pages/ImportExport/` (sibling slice)

## Business context

After a LOTRO update the admin reacts to detected game versions (spec 0001): a forum watcher
(deferred, #85) creates **unprocessed** `GameVersion` rows, and the admin imports a fresh
`exported.txt` against the relevant version. Today the only place versions surface in the Frontend
is the import/export page's target `<select>` — there is **no dedicated screen** to review every
known version (detected vs processed vs superseded) and **no UI to manually register** a version
when the forum scrape is down (spec 0001's "degenerate fallback", exposed by `POST
/api/v1/game-versions` in #107 but never wired into the UI). A manual register is also error-prone
(a typo'd version string), so the admin needs a way to **remove a mistaken, not-yet-used entry**.
#209 closes that gap.

## Goal

An authenticated translator can review every known game version (newest-first, with detection time
and lifecycle status). An admin can manually register a new version when the forum is unavailable,
and delete a still-unprocessed version they registered by mistake.

## In scope

- New Blazor **Static SSR** page `/game-versions` (`Components/Pages/GameVersions/`): lists
  `GET /api/v1/game-versions` newest-first — version string, detected-at, status badge.
- Admin-only **register-version** form (plain `<form method="post">` SSR idiom), shown only when the
  collection advertises the admin-only `register` rel (#158 affordance pattern), POSTing to
  `POST /api/v1/game-versions` through the typed client.
- Admin-only **delete** affordance per row, shown only when the item advertises a `delete` rel
  (emitted for admins on **Unprocessed** versions), POSTing to a new `DELETE /api/v1/game-versions/{id}`.
- **New backend delete slice** `Features/GameVersions/DeleteGameVersion.cs` with a domain guard:
  delete only when the version is `Unprocessed` **and** referenced by no translation; otherwise a
  `Result` conflict failure. `IGameVersionRepository.Delete` + an
  `ITranslationRepository`/read-side "is any translation bound to this version?" check.
- A `GameVersionsLoader` (thin injectable client seam, mirrors `ImportExportLoader`):
  `ListGameVersionsAsync` + `RegisterGameVersionAsync` + `DeleteGameVersionAsync`.
- `Rels.Delete` + the link factory emitting `delete` (item-level, admin + Unprocessed).
- Sidebar nav entry, DI registration, handler/endpoint integration tests + bUnit render tests + loader tests.

## Out of scope

- **Manual status edit** (mark Processed/Superseded) — status is lifecycle-driven by import/approve
  (spec 0001), not manually editable; the version string is the aggregate's identity. Exposing
  manual status edits would contradict the lifecycle model.
- **Deleting a Processed/Superseded or referenced version** — domain-unsafe: a `GameVersion` is
  referenced by translations (`IntroducedInVersion`, `LastSourceChangeInVersion`, `RemovedInVersion`).
  The guard refuses it.
- **Triggering a forum scrape** — no public trigger endpoint in MVP (spec 0001 §contract); the
  watcher runs server-side. The FE only displays its output plus the manual fallback.

## Business rules & edge cases

- The list is open to any translator; the register form / delete buttons show **only** when the
  resource advertises the admin-only `register` / `delete` rel — never a locally recomputed role (#158).
- Duplicate version (normalized per ADR-0003: `48` ≡ `48.0` ≡ `48.0.0`) → the API returns a
  conflict; surface the `ProblemDetails` and keep the list intact.
- **Delete guard (server-side, authoritative):** load the version → not found ⇒ 404; status is not
  `Unprocessed` ⇒ conflict (`CannotDeleteProcessedVersion`); any translation references it ⇒ conflict
  (`CannotDeleteReferencedVersion`); else remove + save ⇒ 204. Under the lifecycle an Unprocessed
  version has never been imported against, so the referenced check is defense-in-depth (it should
  not normally trigger).
- The `delete` rel is emitted per item for admins **only when `Status == Unprocessed`** (cheap,
  status is on the response); the referenced check stays server-side.
- Empty list → friendly empty state; the register form (when admin) still shows so the first version
  can be added.
- Failed list fetch → surface the error; the register form / delete buttons cannot show (rels can't
  be read), mirroring import/export.
- After a successful register or delete, **refresh the list** so it reflects the new state.
- Version input is trimmed; the field carries the same `maxlength` as
  `LotroNotationVersion.VersionMaxLength` (12). Real format validation is server-side (ADR-0003 grammar).

## Contract

- **Page route:** `GET /game-versions` (`[Authorize]`), Static SSR.
- **List:** `GameVersionsLoader.ListGameVersionsAsync` → `GET /api/v1/game-versions` →
  `CollectionResponse<GameVersionResponse>` (keep `Links`).
- **Register:** `GameVersionsLoader.RegisterGameVersionAsync(string version)` →
  `POST /api/v1/game-versions` body `RegisterGameVersionRequest(string Version)` →
  `GameVersionResponse` (201) or `ProblemDetails` (400 / 409-conflict / 422).
- **Delete:** `GameVersionsLoader.DeleteGameVersionAsync(GameVersionId id)` →
  `DELETE /api/v1/game-versions/{id}` (admin) → 204, or `ProblemDetails`
  (404 not found / 409 conflict when Processed/Superseded/referenced).
- **Errors:** new `DomainErrors.GameVersionEntity.OnlyUnprocessedCanBeDeleted` (covers Processed
  **and** Superseded) and `CannotDeleteReferencedVersion` (both `TypeOfError.DataConflict` → 422).
- **Files touched:** new `Features/GameVersions/DeleteGameVersion.cs`, repository `Delete` +
  referenced-check, `Rels.Delete`, the GameVersion link factory; new
  `Components/Pages/GameVersions/{GameVersions.razor, GameVersionsLoader.cs}`; nav link in
  `Components/Layout/NavMenu.razor`; DI in API + `DependencyInjection.cs`; tests on both sides.

## Acceptance criteria

- [ ] `/game-versions` lists versions **newest-first** with version, detected-at, and a status badge.
- [ ] A non-admin translator sees the list but **no register form and no delete buttons**.
- [ ] An admin sees the register form; submitting a valid version POSTs and the new row appears.
- [ ] An admin sees a delete button only on **Unprocessed** rows; deleting removes the row.
- [ ] `DELETE` on a Processed/Superseded version ⇒ conflict; on an unknown id ⇒ 404; on a referenced
      version ⇒ conflict — each surfaced as `ProblemDetails`, list preserved.
- [ ] A duplicate/invalid register surfaces the API `ProblemDetails` without losing the list.
- [ ] An empty list shows the empty state; a failed fetch surfaces the error.
- [ ] The sidebar shows a "Wersje gry" link.
- [ ] Tests: `DeleteGameVersion` handler unit tests (guard matrix) + endpoint integration test (real
      PostgreSQL); `GameVersionsLoader` tests; bUnit render tests (list, admin gating, register, delete, errors).

## Open questions

Resolved with the user (2026-06-28):

1. **Scope** → List + manual Register **+ guarded Delete** of a still-`Unprocessed`, unreferenced
   version (option B). Manual status edit (full CRUD) is rejected — contradicts the lifecycle.
2. **Visibility** → dedicated `/game-versions` page visible to **all translators** (read-only list);
   register **and** delete are admin-only, gated by the `register` / `delete` HATEOAS rels.
3. **Delete guard** → "delete only when `Unprocessed` **and** unreferenced by any translation",
   enforced server-side; the `delete` rel is emitted item-level for admins on Unprocessed versions.

## Assumptions

- Forum-watch auto-detection (#85) stays deferred; versions appear via that future watcher plus the
  manual register. This work does not depend on the watcher existing.
- Polish UI copy, consistent with the existing pages.
