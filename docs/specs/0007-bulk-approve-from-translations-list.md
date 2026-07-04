# Spec 0007: Bulk approve from the translations list (admin)

- **Status:** Agreed
- **Date:** 2026-07-04
- **Author:** koniecdev
- **Ticket:** #322 (Feature: Bulk approve for admin)
- **Related:** Spec 0001 (approve = publish, §review), #101/M2-12 (single `ApproveTranslation`
  slice), #158/spec 0002 (HATEOAS-driven affordances), #321 (Post-Redirect-Get on the editor),
  ADR-0021/PERF-04 (debounced artifact rebuild), `Features/Translations/ApproveTranslation.cs`
  (sibling slice), `Components/Pages/GameVersions/` (admin SSR page pattern)

## Business context

Approving is a per-row reviewer action today: an admin opens the side-by-side editor and clicks
**Zatwierdź** on one translation (#101). After a game update invalidates a batch of rows, or when a
translator submits many drafts, the reviewer has to open and approve each row one at a time. The
translations list (`/translations`) already shows the whole catalog with status filters (e.g.
`NeedsReview`), so it is the natural place to approve several rows at once. #322 asks for checkboxes
on the list so an admin can select rows and approve them in one action.

## Goal

An admin can select several rows on the translations list with checkboxes and approve them all in a
single action, instead of opening each row in the editor.

## In scope

- **New backend slice** `Features/Translations/BulkApproveTranslations.cs`:
  `POST /api/v1/translations/approve` (admin-only) — body `{ ids: [guid, …] }`, returns a summary
  `{ requested, approved, skipped }`. Best-effort: approve every requested row that is currently
  approvable, skip the rest, save once, schedule **one** artifact rebuild.
- **New collection HATEOAS rel** `bulk-approve`, emitted on the translation list's collection
  `Links` **only for admins** (mirrors GameVersions' collection `register` rel); the FE reads the
  POST href from it.
- **Translations list UI** (`Components/Pages/Translations/Translations.razor`, Static SSR): an
  admin-only checkbox column, a checkbox per **approvable** row (a row that advertises the per-item
  `approve` rel), and a "Zatwierdź zaznaczone" submit button that POSTs the selected ids through the
  typed client, then Post-Redirect-Gets back to the list (filters preserved) with an `approved`
  count flash.
- `TranslationListLoader.BulkApproveAsync(href, ids)` (thin client seam).
- `BulkApproveTranslationsRequest` / `BulkApproveTranslationsResponse` in `Contracts`.
- Tests: handler unit (validation + best-effort matrix + save/rebuild), endpoint integration (real
  PostgreSQL), loader test, bUnit render/gating + submit/redirect tests.

## Out of scope

- **Select-all / approve-all-filtered across pages** — a "select all on page" toggle needs client
  JS, which Static SSR forbids; "approve every `NeedsReview` in the whole filter" is a different,
  more powerful feature (a server-side set operation, not checkboxes). The ticket asks for
  checkboxes → per-page manual selection only. Revisit as its own ticket if needed.
- **Bulk of any other transition** (bulk edit, bulk delete) — the ticket names approve.
- **Cross-page selection persistence** — each page render is a fresh GET; checking rows then
  paginating clears the selection (standard SSR behavior).

## Business rules & edge cases

- **Admin-only**, exactly like single approve (`RequireAdminRole`). A translator or anonymous caller
  gets no `bulk-approve` collection rel and no per-row `approve` rels → no checkbox column, no button.
- **Approvable row = the existing per-item `approve` affordance:** a non-removed row in `Draft` or
  `NeedsReview` (the link factory already emits `approve` for exactly these — §`TranslationAggregateLinkFactory`).
  The FE renders a checkbox **only** on rows carrying that rel, so the admin can only select
  genuinely approvable rows — no local role/status recompute.
- **Best-effort, not all-or-nothing.** The selection is a snapshot; between list render and submit a
  row can change (someone else approved it, an import invalidated/removed it). The handler approves
  every requested row that is *still* approvable and **silently skips** the rest — a single stale row
  must never block the whole batch. Rationale: the FE only offers approvable rows, so the happy path
  approves all; skips absorb the race. Consistent with idempotent approve + errors-as-values.
- **What counts as skipped:** an id not found; a row in `Untranslated`/`Approved` (not offered by the
  FE, but tolerated on a stale submit — an already-`Approved` row is a no-op, we do **not** re-stamp
  its approver); a `Draft`/`NeedsReview` row whose `Approve` still fails its domain guard (e.g. a
  draft that was soft-removed) — the domain guard stays authoritative.
- **Ids are de-duplicated** before the DB lookup; `requested` is the distinct count, and
  `approved + skipped == requested` always holds.
- **One transaction, one rebuild.** All approvals are stamped and saved in a single
  `SaveChangesAsync`; the debounced rebuild (PERF-04/ADR-0021) is scheduled **once**, and **only when
  at least one row was approved** (nothing published ⇒ no rebuild). The schedule follows the commit
  (ADR-0021 §1), like the single slice.
- **Reviewer provisioning** is the same first-touch lazy provision as single approve (ADR-0004):
  provision the current identity **once** before the loop; a provisioning failure fails the whole
  request (403) — the batch cannot be attributed to an empty approver.
- **Batch cap = 100**, matching the list's max `pageSize` (a single page is the most a checkbox
  selection can carry). Over the cap ⇒ validation failure (400). Empty ids ⇒ validation failure (400).
- **PRG after submit (#321):** on success the page redirects to the list with the current filters
  preserved plus `?approved=<count>`, so a browser reload is a safe GET and the list re-derives fresh
  links (just-approved rows lose their checkbox). `approved=0` (everything was skipped) shows a
  "nothing approved" note rather than a success flash.
- **Failed POST** (transport / 4xx) → no redirect; surface the `ProblemDetails` on the list, keep it
  intact (mirrors GameVersions' action-error banner).

## Contract

- **Endpoint:** `POST /api/v1/translations/approve` — `RequireAdminRole`. Distinct route from the
  single `POST /api/v1/translations/{id:guid}/approve` (different segment count, no conflict).
- **Input:** `BulkApproveTranslationsRequest(IReadOnlyList<Guid> Ids)` →
  `BulkApproveTranslations.Command(IReadOnlyList<TranslationId> Ids) : ICommand<Result<BulkApproveTranslationsResponse>>`.
- **Output:** `200 OK` `BulkApproveTranslationsResponse(int Requested, int Approved, int Skipped)`
  even when everything was skipped (a well-formed request that published nothing is still a success).
- **Errors:** empty ids / over-cap ⇒ `Validation` (400); provisioning failure ⇒ `Forbidden` (403);
  auth ⇒ 401/403 by policy. No 404/422 — individual non-approvable rows are counted as `skipped`,
  never surfaced as request errors.
- **HATEOAS:** `Rels.BulkApprove = "bulk-approve"` on the list collection `Links` for admins, POST to
  `nameof(BulkApproveTranslations)`.
- **Files touched:** new `Features/Translations/BulkApproveTranslations.cs`; `Rels.BulkApprove`; the
  list slice + `TranslationAggregateLinkFactory`/pagination wiring to emit the collection rel; new
  `Contracts/Translations/BulkApproveTranslations{Request,Response}.cs`; DI in `ApiDependencyInjection`;
  `Translations.razor` + `TranslationListLoader`; tests on both sides.

## Acceptance criteria

- [ ] `POST /api/v1/translations/approve` as admin with a mix of a `Draft` and a `NeedsReview` id
      approves both, returns `{requested:2, approved:2, skipped:0}`, and both appear in the
      distributed file after the rebuild.
- [ ] The same endpoint with a `Draft` id + an `Untranslated` id + an unknown id returns
      `{requested:3, approved:1, skipped:2}` and does not fail.
- [ ] A request whose ids are all non-approvable approves nothing, returns `approved:0`, and
      schedules **no** rebuild.
- [ ] Empty `ids` ⇒ 400; more than 100 ids ⇒ 400.
- [ ] A translator token ⇒ 403; no token ⇒ 401.
- [ ] The list shows a checkbox column and a "Zatwierdź zaznaczone" button **only** for an admin, and
      a checkbox **only** on rows that advertise the `approve` rel.
- [ ] Selecting rows and submitting POSTs the ids to the collection `bulk-approve` href and, on
      success, redirects to `/translations?…&approved=<n>` preserving the active filters.
- [ ] A failed bulk POST surfaces the error and leaves the list intact (no redirect).
- [ ] Tests: `BulkApproveTranslations` handler unit tests (validation + best-effort matrix + single
      save + single rebuild + schedule-after-commit) and endpoint integration tests (real
      PostgreSQL); `TranslationListLoader.BulkApproveAsync` test; bUnit render/gating + submit/redirect
      tests.

## Open questions

None left open for the user. The ticket ("bulk approval from the translations list for admin,
checkboxes for approve") is crisp; every remaining choice is **derived from existing repo
conventions**, not a fresh business decision, so the worker resolved them inline:

1. **Best-effort vs all-or-nothing** → best-effort. Derived from the link-driven affordance model
   (#158 — the FE only offers approvable rows) + idempotent approve + errors-as-values. Rejected
   all-or-nothing: one stale row blocking a 50-row batch is bad UX and inconsistent with those
   patterns.
2. **Response shape** → counts only (`requested/approved/skipped`). Rejected a per-id skip-reason
   breakdown as YAGNI — the flash only needs the approved count; a breakdown is an additive change
   if ever wanted.
3. **Where the FE reads the POST url** → a collection `bulk-approve` rel (like GameVersions'
   `register`), not a hardcoded path. Rejected hardcoding as inconsistent with #158.
4. **Batch cap** → 100, from `ListTranslations`'s max `pageSize` (a checkbox selection can't exceed
   one page). Rejected "unbounded" (a pathological `IN (…)` footgun) and an arbitrary magic number.
5. **Post-submit UX** → Post-Redirect-Get with the filters preserved + an `approved` count flash,
   following the just-landed #321 precedent. Rejected in-place reload (re-post on refresh).

## Assumptions

- Polish, one-language catalog (as everywhere else in the UI today).
- The list page stays `[AllowAnonymous]` + Static SSR: the whole bulk affordance is server-gated by
  the admin-only rels, so no page-level auth change is needed (the typed client carries the admin's
  bearer token server-side).
