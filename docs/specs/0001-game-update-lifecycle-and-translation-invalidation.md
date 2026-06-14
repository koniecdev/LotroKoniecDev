# Spec 0001: Game-update lifecycle — GameVersion, import diff, translation invalidation, distribution

- **Status:** Agreed
- **Date:** 2026-06-11 (drafted and agreed same day — all six extracted decisions resolved by the user)
- **Author:** Artur Koniec (domain brain dump) — structured against code + knowledge base by Claude
- **Ticket:** re-cuts #93 (M2-04 Domain), #97 (M2-08 import), #100 (M2-11 upsert), #101 (M2-12 approve),
  #102 (M2-13 export/download), #85 (forum cron), #28 (M2-17 bootstrap); new tickets per Impact section
- **Related:** ADR-0002 (two bounded contexts), `docs/knowledge-base/update-detection-strategy.md`
  (partially superseded here — vnum/crowdsource parts), `dat-export-diff-2026-03-22.md`,
  `dat-protection.md`, `vnum-observations.md`, CLAUDE.md → Roadmap M2

## Business context

The whole platform exists to translate LOTRO into Polish. All game texts live in one ~2 GB DAT
file; the CLI `export` dumps every text fragment into `exported.txt` (one row per fragment).
Each game update (SSG patch) **adds new texts, rewords existing ones, and removes others** —
empirically ~5k changed lines across 47.1→47.2 and +4,231 fragments at 48.0. A Polish translation
written against the old English is **stale the moment SSG rewords the source**: re-applying it
would mask a lore/quest correction with outdated Polish. The TMS must therefore absorb each new
game version's export, detect exactly what changed, invalidate affected translations (falling
back to fresh English in game), and serve patchers an always-safe translation file.

## Goal

After a LOTRO update, the admin uploads one fresh `exported.txt`; the system works out what SSG
added/changed/removed, parks stale Polish for re-translation instead of shipping it, and every
player's patcher downloads a translation file that never shows outdated text in game.

## Ubiquitous language

| Term | Meaning |
|---|---|
| **Translation** | One text fragment row of the `\|\|` contract / one DB row: identity `(FileId, GossipId)`, English source text, optional Polish content, args metadata, status. The atom of the whole domain. |
| **GameVersion** | Aggregate root. One forum-announced LOTRO version (e.g. `48.0`). Carries detection + processing state (`IsProcessed`); the unit the admin reacts to. |
| **Diff** | The comparison of an uploaded `exported.txt` against the last processed source state, per `(FileId, GossipId)`, over the **full** file, on **every** processed version. Outcomes: added / source-changed / removed / unchanged. |
| **Invalidation** | Marking a Polish translation stale because its English source changed. Invalidated rows are excluded from the distributed file → game shows the fresh English (fallback-to-English). |
| **Baseline import** | The very first `exported.txt` import: no diff partner, every row lands as untranslated English source. |
| **Source text** | The English original of a fragment, as exported from the DAT. The thing the diff compares. |
| **Translation file** | The distributed artifact in `polish.txt` format: Approved, non-invalidated rows only. What the CLI `patch` consumes. |
| **`exported.txt`** | Full English dump from CLI `export`: one row per fragment, `\|\|`-separated, ~780k+ lines. |
| **Piece separator / placeholder** | `<--DO_NOT_TOUCH!-->` — literally `DatFileConstants.PieceSeparator`. Marks argument insertion slots inside a text; never translated. |
| **Forum version** | Version string scraped from lotro.com release notes — the **only** reliable game-version signal (DAT vnum is dead, `vnum-observations.md`). |

## In scope

- `GameVersion` aggregate root in `TranslationSystem.Domain` (extends #93).
- TMS-side forum watcher (hosted service cron) that detects a new forum version and creates an
  unprocessed `GameVersion` — the DB state change the admin reacts to (re-cuts #85: de-vnumed,
  forum-only, no crowdsource confirmation).
- Import slice (#97) re-shaped: upload is **bound to an unprocessed GameVersion** and triggers
  the diff (insert added / update source + invalidate changed / handle removed / skip unchanged);
  processing flips `IsProcessed`.
- Invalidation state on Translation + its interplay with upsert (#100) and approve (#101).
- Distribution endpoint (re-shapes #102): current translation file for the newest processed
  version — anonymous, cacheable, cache-stampede-proof.
- Baseline init flow: first import + initial GameVersion from the current forum version;
  `polish.txt` seed (#28) layered on top as Approved.
- CLI auto-download (decided): the end user manually downloads **only the CLI itself** — `launch`
  fetches the current translation file from the distribution endpoint, caches it locally, and
  refreshes it automatically whenever the artifact changes. Additive patcher slice sanctioned by
  the ADR-0002 freeze amendment; ticket M2-20. The WPF app (M4) wraps the same flow in a GUI.

## Out of scope

- **Automating game updates on a VM to regenerate exports** — explicitly rejected (cost); the
  admin's own LOTRO install + manual upload is the pipeline.
- Crowdsourced game-version reports & two-source confirmation (#84, #31) — post-MVP as labeled;
  the forum is the sole detection source for now.
- `TranslationHistory` / full audit trail of content changes (#50, post-MVP). This spec stores at
  most a single previous-source snapshot per row (Q3), not a history table.
- Per-version snapshots of all translations — anti-bloat rule below.
- Any change to the `||` file format itself (ADR-gated contract; this spec only *consumes* it).
- Multi-language beyond `pl` (the shape allows it; no second language ships now).

## Business rules & edge cases

### Identity & the exported row (what can and cannot change)

- A text fragment's identity is the pair **`(FileId, GossipId)`** — `FileId` (int) addresses the
  DAT subfile, `GossipId` (`Fragment.FragmentId`, 8-byte unsigned) the fragment within it. This
  is how LOTRO itself addresses texts and how the patcher writes them back. TMS stores GossipId
  as 64-bit (`long`, already mandated by #93).
- An exported row is exactly: `file_id||gossip_id||text||args_order||args_id||approved`
  (`ExportTextsQueryHandler.cs:95`). On export: `text` = English pieces joined with
  `<--DO_NOT_TOUCH!-->`, `\r`/`\n` escaped to literals; `args_order`/`args_id` = `NULL` when the
  fragment has no arguments, otherwise the identity order `1-2-…-N`; `approved` = constant `1`.
  There is **no other state in a row** — no hidden version, no category, no UI grouping; the DAT
  is one undifferentiated bag of texts.
- Across game versions, for a given `(FileId, GossipId)`: the **text may be reworded**, the
  **argument structure may change** (placeholder count/order), the row may be **removed**; and
  new pairs **appear**. The pair itself is stable for continuing texts — empirically proven: our
  8 live translations were re-found at identical IDs after updates incl. major 48.0
  (`dat-protection.md`, `live-test-2026-04-23.md`); the 47.1→47.2 window showed +7,086 added /
  −2,319 removed lines with rewordings like `Vitals` → `Player Vitals`
  (`dat-export-diff-2026-03-22.md`).
- ID re-use (delete + later re-add of the same pair with different meaning) is indistinguishable
  from a removal followed by an addition — the diff treats it as exactly that.

### GameVersion lifecycle

- The forum watcher polls the lotro.com release-notes page (same regex the patcher's
  `UpdateChecking` feature uses; the TMS **duplicates** the scraping logic — bounded contexts
  share no code, ADR-0002). A newly seen version creates `GameVersion { Version, DetectedAt,
  IsProcessed = false }`. Detection does nothing else — no diff, no invalidation.
- `Version` is stored as the raw forum string (`"48.0"`, `"47.1.1"`); ordering = `DetectedAt`
  (the forum is chronological); no semver parsing.
- The admin sees unprocessed versions and reacts: updates their own LOTRO, runs CLI `export`,
  uploads the file **for that GameVersion**. Upload-and-diff is what processes a version —
  `IsProcessed = true` only after the diff committed.
- **Stacked unprocessed versions** (e.g. 48.1 detected while 48.0 still unprocessed): the admin
  uploads only the newest; the diff is inherently cumulative (proven: the 2026-03-22 diff spanned
  3 accumulated patches and behaved identically, `dat-export-diff-2026-03-22.md`). Intermediate
  versions are marked superseded/skipped — they will never receive an upload.
- Re-upload to an already processed version is allowed and **idempotent** (admin uploaded a wrong
  file and corrects it; same guarantee #97 already demands).
- *(Amendment 2026-06-12, M2-04 Phase A)*: `IsProcessed` + the superseded/skipped marker are
  realized in code as a single `GameVersionStatus` enum (`Unprocessed | Processed | Superseded`) —
  the same no-parallel-bool philosophy as the Translation status model below, making
  processed-and-superseded unrepresentable.

### Diff semantics (runs only on upload, over the full file)

For every row of the uploaded export, compared against the stored source state by
`(FileId, GossipId)`:

- **Added** (pair unknown): insert as untranslated — English source only, no Polish, available
  for translation. Stamp `IntroducedInVersion`.
- **Source-changed** (pair known, source content differs — comparison covers text **and** args
  columns as one unit, since a placeholder-structure change is a meaning change even if rare
  without a text change): overwrite the stored English source with the new one; **invalidate**
  any existing Polish translation; stamp `LastSourceChangeInVersion`.
- **Removed** (stored pair absent from the upload): soft-mark with `RemovedInVersion` — excluded
  from translation work and from the distributed file, never hard-deleted (pointer-only cost,
  keeps "what did this patch kill" visible).
- **Re-added** (a previously soft-removed pair reappears in the upload): clear `RemovedInVersion`;
  if the incoming source is identical to the stored one, the row's previous status — including
  `Approved` — is restored as-is (the old Polish is still valid); if it differs, the
  source-changed rule applies.
- **Unchanged** (pair known, source identical): no-op. *No diff = SSG touched nothing = the
  stored state stands* — sound because `export` is a deterministic full dump of all text subfiles.
- The diff transaction is all-or-nothing; a failed upload leaves the previous state intact.
- **Truncation guard (both, decided):** a partially written/corrupt `exported.txt` would
  masquerade as a mass removal (and mass invalidation), so the import defends itself twice —
  the upload is **rejected when any line fails to parse** (stricter than the patcher's
  warn-and-skip, because on import a skipped line is indistinguishable from a removed row), and
  **rejected when the would-be-removed fraction exceeds a threshold** (default 20%, configurable)
  unless the admin passes an explicit override flag (legitimate mass cuts happen — SSG removed
  2,319 lines in one window).

### Invalidation & fallback-to-English (the physics)

- "Fallback to English" requires **no write of English into the Polish field**. Two mechanisms
  compose: (1) the game launcher has *already* chunk-patched the new English into the player's
  DAT (`dat-protection.md`); (2) the TMS excludes invalidated rows from the translation file, so
  `patch` never re-applies stale Polish over it. This is the codified form of the knowledge-base
  core insight "re-patching after game update is WRONG" (`update-detection-strategy.md`).
- An invalidated translation is **explicitly visible to the admin/translators** as needing
  re-translation; supplying and approving a new Polish text clears the invalidation (the approve
  slice #101 gains this rule). The stale Polish is **kept** as the draft starting point, and the
  superseded English is stored in a single `PreviousSourceText` column so the translator sees
  old-vs-new source side by side — one column, not a history table (#50 stays post-MVP).
- **Multi-update-before-review:** when a row is reworded by several patches before anyone reviews
  it, `PreviousSourceText` is **frozen at the first invalidation** — the English the still-current
  Polish was written against — not overwritten by each intermediate source the translator never
  saw. It refreshes only when the row is re-drafted (upsert against the new English), so it always
  tracks the baseline the current Polish actually corresponds to. Per-patch history stays #50,
  post-MVP.
- Worst case is by design: every patch may invalidate some translations; that is the system
  working, not failing.

### Storage — anti-bloat rule

- **One mutable row per `(FileId, GossipId)`** — never a copy of all translations per game
  version. Versions are referenced by pointers on the row (`IntroducedInVersion`,
  `LastSourceChangeInVersion`, possibly `RemovedInVersion` per Q2), giving per-version grouping
  ("what did 48.0 change?") without duplicating 780k rows each patch.
- Status model (decided): a single status enum `Untranslated | Draft | Approved | NeedsReview` —
  no parallel invalidation bool, so illegal combinations (`Approved` **and** invalidated) are
  unrepresentable. Invalidation = transition to `NeedsReview` (+ `PreviousSourceText` set);
  approve = `Draft`/`NeedsReview` → `Approved`. #93's "add states only when a slice needs them"
  is now satisfied: this spec is the slice that needs it.

### Distribution — the cacheable translation file

- One endpoint serves the **current translation file for the newest processed GameVersion**:
  Approved, non-invalidated, non-removed rows, serialized in the exact `polish.txt` contract
  format (escaping, `NULL` args, sorted by `FileId` then `GossipId` — byte-compatible with
  `TranslationFileParser`, #102's golden-fixture criterion stands).
- Anonymous (players' patchers hold no accounts), **cacheable, and stampede-proof**: the file is
  regenerated **on write** (approve/processing), never on request — the endpoint always streams a
  pre-built artifact with an `ETag`/content-hash, so a thundering herd after an update hits static
  bytes, and `If-None-Match` turns no-change polls into 304s. Regeneration triggers (decided):
  every write that changes the distributed set — approve, upsert affecting an `Approved` row,
  version processing — and nothing else; players receive newly approved translations without
  waiting for a game patch.
- The CLI is the end-user client (decided): on `launch` it asks the endpoint with
  `If-None-Match` of the cached artifact's ETag — 304 → proceed with the cached file (the proven
  255 ms skip path), 200 → save the new file, then the existing hash flow patches only on change
  (`update-detection-strategy.md` — the hash flow "naturally evolves into the API model").
  API unreachable/offline degrades gracefully to the cached file; launch never blocks on the
  network.

### Bootstrap (first run)

- Init seeds the current forum version as the initial `GameVersion` and performs the **baseline
  import** (no diff — everything is added/untranslated). `IsProcessed = true` once loaded.
- The existing production `translations/polish.txt` is then seeded as Approved (#28) — it merges
  Polish content onto baseline rows by `(FileId, GossipId)`, it does not create rows of its own.

## Contract

- **Detection:** TMS hosted-service cron (interval configurable) → creates unprocessed
  `GameVersion`. No public trigger endpoint in MVP (an admin-only manual "register version"
  endpoint is the degenerate fallback if the forum scrape breaks).
- **Admin views pending work:** `GET /api/v1/game-versions` (authorized) — versions with
  detection/processing state.
- **Upload & diff:** re-shaped #97 — `POST /api/v1/game-versions/{id}/import` (multipart
  `exported.txt`, authorized: admin). Response: `ImportSummary { Added, SourceChanged,
  Invalidated, Removed, Unchanged, Warnings }`. Errors as ProblemDetails via `Result` failures
  (validation → 400, unknown version → 404, truncation guard → 422).
- **Distribution:** re-shaped #102 — `GET /api/v1/translation-files/{lang}` (anonymous, `pl`
  only for now) → streamed translation file + `ETag`; honors `If-None-Match` → 304. (Route
  follows the brain dump's `/translation-files/pl`; supersedes #102's
  `/translations/export?lang=pl`.)
- **CLI sync (M2-20, patcher context):** the launch flow gains a download step ahead of the
  existing hash check — `GET /api/v1/translation-files/pl` with `If-None-Match`; 200 → save file
  + ETag, 304/unreachable → keep the cached file and proceed. Configurable API base URL; the
  manual `patch <name>` path stays untouched. Additive slice + its launch-flow wiring are the
  sole sanctioned freeze exception (ADR-0002 amendment).
- **Files:** the `||` format is consumed/produced unchanged — this spec triggers **no** ADR-gated
  format change. Golden fixtures + round-trip tests on both sides stay the drift guard.
- **Editor loop:** #98–#101 (list/get/upsert/approve) operate on the same Translation rows;
  approve additionally clears invalidation; list gains a "needs re-translation" filter.

## Acceptance criteria

- [ ] New forum version ⇒ unprocessed `GameVersion` appears (cron detection; no other side effect).
- [ ] Baseline import on empty DB loads every parseable row as untranslated English source bound
      to the initial GameVersion.
- [ ] Upload for an unprocessed version: added rows appear untranslated; source-changed rows get
      the new English **and** their Polish becomes invalidated; removed rows leave the
      distributed file; unchanged rows are byte-for-byte untouched (incl. timestamps).
- [ ] An invalidated row never appears in the distribution endpoint output until re-approved.
- [ ] Identical re-upload is a no-op (idempotent); the diff is all-or-nothing on failure.
- [ ] A truncated upload does not mass-invalidate (guard per Q4) — test with a cut-off fixture.
- [ ] Distribution output parses byte-identically with the patcher's `TranslationFileParser`
      (golden fixture, round-trip: import → approve → download → patcher parse).
- [ ] Distribution endpoint: anonymous, serves pre-built artifact, correct `ETag`/304 behavior,
      no per-request regeneration (stampede-proof by construction).
- [ ] Approving a translation makes it appear in the next download; approving an invalidated row
      clears invalidation.
- [ ] Skipped/superseded intermediate versions: uploading only the newest of several unprocessed
      versions yields a correct cumulative diff (mirror of the empirically proven 3-patch case).
- [ ] Re-adding a removed pair: identical source restores the previous status (incl. Approved);
      changed source lands as `NeedsReview` with `PreviousSourceText` set.
- [ ] CLI launch sync (M2-20): 304 → cached file used, no download; API unreachable → launch
      proceeds on the cached file; 200 → new file saved and the hash flow patches.

## Open questions

### Empirical — answered from code/knowledge base

- *What identifies a row, and what can change across updates?* Answered — see Business rules →
  Identity (sources: `ExportTextsQueryHandler.cs:95`, `TranslationFileParser.cs`,
  `dat-export-diff-2026-03-22.md`, `live-test-2026-04-23.md`).
- *Is the forum really the only version source?* Yes — vnum definitively schema-only
  (`vnum-observations.md`); nothing in game files identifies content version.
- *Does skipping intermediate versions break the diff?* No — accumulated diffs behave identically
  (3-patch window proven, `dat-export-diff-2026-03-22.md`).
- *Why is exclusion enough for English fallback?* Launcher chunk-patching already delivers fresh
  English; only re-applying stale Polish could mask it (`dat-protection.md`,
  `update-detection-strategy.md`).
- *Do real GossipIds exceed `int.MaxValue`?* `[needs verification]` — scan a real `exported.txt`
  on the Windows box (`awk -F'\|\|' 'max<$2+0{max=$2+0}END{print max}'`). Export writes raw
  8-byte `FragmentId`; the **patcher's** parser does `int.Parse` (`TranslationFileParser.cs:86`)
  and would warn-skip larger IDs — dormant today, candidate patcher **bugfix** ticket (allowed
  under freeze) if the scan finds large IDs. TMS uses `long` regardless (#93).

### Business decisions — resolved by the user, 2026-06-11

- **Q1 — Translation-file refresh trigger:** regenerate on every approve / upsert affecting an
  `Approved` row **and** on version processing. ETag+304 keeps polling cheap; players get newly
  approved translations without waiting for a game patch.
- **Q2 — Removed rows:** soft-mark `RemovedInVersion`, excluded everywhere, never hard-deleted;
  reversible when SSG re-adds the pair.
- **Q3 — Invalidated row contents:** keep the stale Polish as the draft starting point; store the
  superseded English in a single `PreviousSourceText` column for side-by-side context.
- **Q4 — Corrupt/truncated upload guard:** both guards — reject on any parse error **and** on
  removed-fraction above the configurable threshold (default 20%) without an explicit admin
  override.
- **Q5 — Who downloads:** the CLI is the end-user client — the only thing a player downloads
  manually is the CLI itself; `launch` auto-downloads, caches and refreshes the translation file
  (ETag). Additive patcher slice (M2-20) sanctioned via the ADR-0002 freeze amendment; the WPF
  app (M4) is a GUI over the same flow.
- **Q6 — Invalidation modeling:** single status enum `Untranslated | Draft | Approved |
  NeedsReview`; no parallel bool.

## Assumptions

- The admin's own LOTRO install is the sole export source; export runs are manual (no VM/CI
  automation — explicitly rejected on cost).
- `export` is deterministic and complete per game state: absence of a textual diff means SSG
  changed nothing (basis of the unchanged-rule).
- The forum scrape (regex on release notes) keeps working; if SSG redesigns the page, detection
  degrades gracefully to the manual fallback, never to false versions.
- One language (`pl`) ships; identity, statuses and routes are language-scoped so a second
  language is additive.
- Translation volume stays in the patcher-proven envelope (~780k rows, tens of MB) — streaming +
  pre-built artifact handles it; no pagination/chunking of the file itself.

## Impact on existing docs & backlog (applied 2026-06-11)

- **CLAUDE.md** — Roadmap M2 extended with the update-lifecycle slices (GameVersion + forum
  watcher + diff-on-import + distribution artifact + CLI sync); M4 reframed as GUI over the same
  flow; freeze wording references the amendment; routing table points at this spec.
- **ADR-0002** — amended in place (rides the same unmerged PR #90 branch): §2 freeze admits the
  single additive M2-20 distribution-consumer slice; Alternative E wording extended to the CLI.
- **`docs/knowledge-base/update-detection-strategy.md`** — header supersede pointer added: the
  vnum/crowdsource "Docelowy Model" is superseded by this spec (forum-only detection,
  status-based invalidation); the core insight and admin-flow sections remain valid and are
  codified here.
- **Issues re-cut:** #93 (GameVersion aggregate + status enum + transitions), #97 (version-bound
  upload + diff + truncation guard; route `POST /game-versions/{id}/import`), #102 (route
  `/translation-files/{lang}`, pre-built artifact + ETag/304, regenerate-on-write), #85
  (post-MVP M3-11 → **M2-18**: forum-only watcher creating `GameVersion`, vnum/`GameVersionReport`
  confirmation dropped), #101 (approve clears invalidation + triggers artifact regeneration),
  #100 (upsert on `NeedsReview` → `Draft`, `PreviousSourceText` preserved until approve), #98
  (NeedsReview filter; removed rows hidden by default), #28 (merge-only onto baseline rows),
  #104 (update-cycle integration scenario).
- **New tickets:** **#107** (M2-19) GameVersion endpoints (list + manual register fallback),
  **#108** (M2-20) CLI auto-download (launch sync; freeze amendment), **#109** (M1-15) GossipId
  int-overflow verification/hardening `[needs verification]`.
- **Post-MVP unchanged:** #84/#31 (crowdsource reports), #50 (TranslationHistory), #30 (XML
  contexts), #38/#39 (glossary/UX).
