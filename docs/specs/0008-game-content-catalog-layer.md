# Spec 0008: Game-content catalog — catalog entries as translation units (LOTRO Companion import)

- **Status:** Agreed (drafted and agreed same day — all five extracted decisions resolved by the user;
  naming amended same day: **CatalogEntry, never "entity"** — the term collides with DDD's Entity
  and this layer is deliberately *not* domain-modeled)
- **Date:** 2026-07-06
- **Author:** Artur Koniec (vision) — researched & structured by Claude
- **Ticket:** epic **#362** (M7-00) + #363–#375 (M7-01…M7-13), milestone "M7: Game-content
  catalog (LOTRO Companion)"; supersedes #30 (M2-08 — closed) and the quest-browser half of #38
  (M3-07 — re-scoped to glossary)
- **Related:** `docs/knowledge-base/lotro-companion-data-model.md` (the empirical foundation —
  read it first), spec 0001 (update lifecycle — untouched by this spec), ADR-0002 (bounded
  contexts), ADR-0007 (projections are not aggregates), ADR-0011 + spec 0006 (COPY/streaming
  import idiom), ADR-0021 (artifact rebuild — explicitly NOT triggered here), ADR-0023
  (forward-only migrations)

## Business context

The TMS holds ~792k flat translation rows keyed `(FileId, GossipId)` — spec 0001 §Identity is
explicit that the DAT is "one undifferentiated bag of texts" with no category or grouping. A
translator therefore cannot work the way translation actually happens: pick *a quest*, translate
its name, description, objectives, dialogs and progress texts as one coherent unit, and move to
the next. Both prior-art projects solved this the same way — LOTRO Companion (and the Russian
platform built on its data) organizes game texts per game object. Companion's published XML
preserves our exact row identity as literal `key:<FileId>:<GossipId>` tokens on every
translatable field, with semantic roles derivable from XML structure — a **deterministic,
zero-heuristic join** (verified in code, in their published data, and against our live 48.7
export; see the knowledge-base doc). This spec imports that structure as a **catalog** over the
flat rows.

## Goal

A translator (or anonymous visitor) can browse the game-content catalog (quests, deeds, …), see
per-entry and per-category translation progress, open one entry, and translate/approve **all of
its texts as a unit** — while the flat `/translations` list keeps serving search and the rows no
catalog entry covers.

## Ubiquitous language

| Term | Meaning |
|---|---|
| **CatalogEntry** | One game object a player recognizes: a quest, deed, item, NPC, skill, trait, title, emote, faction, dungeon, mob, … Identified by its game **DID** (`1879xxxxxx`, 0x70-band int). Imported reference data — never user-edited. **Named deliberately: not "entity"** (collides with DDD's Entity; this layer is not domain-modeled — see the M7-01 ADR). |
| **Kind** | The entry's type (`CatalogKind`, string-backed, registry-driven). **Every Companion lore file that carries `key:` tokens is a kind** (decided Q1); quests + deeds ship first, the rest follow via the harvester registry within the same epic. |
| **TextSlot** | One translatable position within an entry: `(Role, ObjectiveIndex?, SortOrder)` → `(FileId, GossipId)`. The join to the Translation row is **logical by key**, never an FK — the row may not exist (dangling). |
| **Role** | The slot's semantic function. **Curated taxonomy for quests/deeds** (derived from XML structure): `Name`, `Description`, `Bestowal`, `Objective`, `ObjectiveDialog`, `ObjectiveProgress`, `Billboard`, `LoreInfo`; **attribute-derived for registry kinds** (e.g. `description`, `pluralName`, `title`). Stored as a bounded string. |
| **Harvester registry** | Code-side (static) per-file config for the long-tail kinds: file name → kind, record element, id/name/category attributes. One registry row + one fixture per kind — no per-kind parser code. |
| **Companion dataset** | One uploaded snapshot of LOTRO Companion's `lore/*.xml` files (e.g. "Update 48.5"). Replace-per-kind on import; carries a freeform label + import stats. |
| **Dangling slot** | A slot whose `(FileId, GossipId)` matches no live Translation row (version skew or our soft-removal). Kept, counted, hidden from progress denominators. |
| **Membership** | The reverse view: the catalog entries (+roles) a given translation row belongs to. One row can belong to many entries; most rows belong to none ("uncategorized"). |
| **Entry progress** | Per-entry counters over its non-dangling slots: `Total / Approved / Draft / NeedsReview / Untranslated`. "Complete" = all Approved. |

## In scope

- **Reference-data model** (new tables, additive migration): `CatalogEntries` (DID PK, Kind, Name,
  Category, Level) + `CatalogTextSlots` (EntryDid, Role, ObjectiveIndex?, SortOrder, FileId,
  GossipId) + `CompanionDatasets` (import audit: label, counts, ImportedAt, ImportedById).
  Modeled as **imported reference data, not a DDD aggregate** (no behavior, no user mutation;
  ADR-0007's spirit) — the modeling decision gets its own ADR at implementation start (M7-01).
- **Import slice** `POST /api/v1/catalog-entries/import` (Admin): multipart **zip** upload
  containing any subset of Companion's `lore/*.xml` files (+ optional `enums/*.xml`,
  `labels/en/enum-*.xml` for category names). Streaming parse (`XmlReader`), all-or-nothing
  **replace per imported kind** in one transaction, bulk write via a COPY port (ADR-0011 idiom),
  summary with dangling/matched counts per kind.
- **Two-tier parsing (decided Q1 — "everything with tokens"):** tier 1 = curated mappers for
  `quests.xml` and `deeds.xml` (rich role taxonomy, objective structure); tier 2 = the
  **harvester registry** for every other token-bearing lore file (items, NPCs, skills, traits,
  titles, emotes, factions, dungeons, mobs, effects, recipes, sets, …): entry = the registry's
  record element (DID + inline name), slots = its `key:` token attributes with attribute-derived
  roles. Files with no tokens (e.g. `geoAreas.xml` — inline names) are not kinds.
- **Read surface** (anonymous read, mirroring `/translations` browsability):
  `GET /api/v1/catalog-entries` (kind/category/search/progress filters, paged, per-entry progress
  counters), `GET /api/v1/catalog-entries/{did}` (ordered slots joined to translation rows),
  `GET /api/v1/catalog-entries/categories?kind=`, entry **memberships on
  `GET /api/v1/translations/{id}`**, and `GET /api/v1/catalog-entries/stats` (coverage by
  kind+category — dashboard food).
- **Frontend (Blazor SSR, purity-gated):** catalog browser page (`/catalog` — kind dropdown, not
  tabs: the kind list is registry-sized), catalog entry page (`/catalog/{did}`: slots grouped by
  role with status badges, links into the existing editor, **entry-scoped bulk approve** posting
  to the existing bulk endpoint), editor gains catalog context (breadcrumb + prev/next slot +
  "next untranslated in this entry").
- **Attribution (decided Q4):** a visible credit in the frontend footer/about — "catalog
  structure data: LOTRO Companion" with a link; reaching out to the maintainer (Discord/forum) is
  an epic-level human checklist item for Artur, not a code ticket.
- **Tests:** golden Companion XML fixtures (trimmed real quests/deeds incl. an argful text and a
  multi-objective quest), parser unit tests, import/list/detail/membership integration tests
  against real PostgreSQL, one E2E browser scenario (import → browse → translate a quest
  atomically → bulk approve → artifact contains the rows).

## Out of scope

- **Name slots for kinds whose XML carries no name token.** Quests/deeds emit `rawName`
  (`key:T:K`); some kinds (observed: items) expose the name only as an inline string + DID-keyed
  label, which carries no `(FileId, GossipId)`. Those kinds get description/pluralName/… slots
  but **no Name slot** — and there is **no text-matching fallback** (join on keys, never text).
  Honest per-kind coverage; revisit only if a deterministic name source appears.
- **Auto-fetch from the `lotro-data` GitHub repo** — decided (Q2): the epic ships
  admin-uploads-a-zip, same trust and offline model as `exported.txt`. A "fetch latest from
  GitHub" button is a named fast-follow after the epic.
- **Wiki features** — rewards, prerequisites, quest chains/arcs, maps, icons, NPC pages.
  We import *text structure*, not the game encyclopedia.
- **Glossary** (#31) — unchanged, separate backlog item. The old #38 bundled it; this spec
  supersedes only the quest-browser half of #38.
- **`labels/pl` reverse export to Companion** — strategically sweet (their RU/ES precedent shows
  the mechanism), explicitly post-MVP; noted in the knowledge-base doc.
- **Per-game-version catalog history** — one current dataset, replace-per-kind; no snapshots
  (anti-bloat, mirrors spec 0001 §Storage).
- **No change** to the `||` contract, the patcher, the import/diff of `exported.txt`, statuses,
  approve/upsert semantics, or the distribution artifact. The catalog is a **lens**: it never
  mutates translations. Catalog import does **not** trigger an artifact rebuild (ADR-0021's
  triggers are unchanged — the distributed set is untouched).

## Business rules & edge cases

### Import & dataset lifecycle

- Upload is a zip; the importer locates known file names at any depth (`quests.xml`, `deeds.xml`,
  every registered harvester file, enum/category files). Unknown zip entries are ignored. A zip
  containing **no** known token-bearing file → 422 (nothing to import). Present kinds are
  imported; absent kinds keep their current data (**replace-per-kind**, so a quests+deeds-only
  zip never wipes items and vice versa).
- All-or-nothing: any XML parse failure, malformed token (`key:<int>:<int>` regex), or DID
  outside the positive-int range rejects the whole upload (422, ProblemDetails with collected
  errors capped like the text import). No partial datasets.
- Re-upload of the same zip is idempotent (replace-per-kind is inherently so). Concurrent import
  requests: last-commit-wins inside the transaction; imports are admin-only and rare (per game
  update) — no extra locking beyond the transaction.
- Unknown *attributes/elements inside known files* are skipped silently (their schema evolves —
  e.g. new reward types must not break us); only the whitelisted role-bearing attributes are
  read. A file yielding **zero** entries is treated as malformed (422), not as "delete all".
- Entry fields: `Name` = inline English `name=` attribute (display convenience; its *translation*
  lives in the `Name` slot via `rawName`'s token). `Category` = resolved English enum label when
  the enum files are present in the zip, else the raw code as string. `Level` = `level=` when
  present.
- The dataset row records: freeform label (admin-entered, e.g. "24.9 / U48.5"), counts
  (entries, slots, matched, dangling, per kind), `ImportedAt`, `ImportedById`. History appends;
  the newest row is "current" (display-only metadata — slots/entries themselves are the truth).
- **No GameVersion coupling.** Companion data lags or leads our export by design (their 48.5/48.8
  vs our 48.7 — both observed). Version skew materializes as dangling slots, which is normal
  operation, not an error. The dangling fraction in the summary is the admin's health signal.

### The join & the lens invariant

- Slot → row join is **by `(FileId, GossipId)`** against the existing unique index — never by
  text (their argument rendering differs: `${PLAYER}` vs `<--DO_NOT_TOUCH!-->`), never by FK.
- Dangling slots (no live row, or the row is soft-removed `RemovedInVersion != null`): rendered
  greyed-out in the entry view, **excluded from progress denominators**, counted in the import
  summary and the entry payload (`MissingSlots`).
- One row may appear in many entries (shared strings) — memberships are a list. Rows with no
  membership remain exactly where they are today: the flat list. **Nothing about the flat
  workflow regresses.**
- The layer never writes to `Translations`. Upsert/approve/bulk-approve flows are reused as-is
  (entry bulk approve posts translation ids to the existing endpoint; quests average ~12 slots,
  and if an outlier ever exceeds the endpoint's 100-id cap, the frontend loader posts in batches —
  no endpoint change).

### Browsing & progress

- Catalog list default sort: `Category, Level, Name` with `Did` tiebreaker (total order — the
  AUDIT-EF-03 lesson); sortable by name/level/category/progress; offset pagination mirroring
  `ListTranslations` (`PageSize` clamp 1..100).
- Filters: `kind` (single-select dropdown, default `Quest` — the kind list is registry-sized, so
  no tabs), `category`, `search` (entry **name**, trigram ILIKE), `progress`
  (`untouched | inProgress | complete`).
- **Progress bar = Approved / total live slots (decided Q3)** — "done" means shippable in the
  distributed artifact. Draft and NeedsReview appear as secondary counters, never in the bar.
- Progress counters are computed on read (GROUP BY join over the page's entries — bounded by
  page size × slots/entry); the coverage **stats** endpoint aggregates by kind+category. If the
  landing/stats query ever proves hot, caching follows the #354 pattern — not built now (YAGNI).
- Anonymous visitors get the same read-only browse the flat list already gives (decided Q5);
  HATEOAS action links appear per role exactly like `ListTranslations` does today.

### Editor integration

- Editor called with `?entry=<did>` renders the catalog breadcrumb ("Quest: The Bird and Baby →
  Objective 1 dialog"), prev/next slot links in `SortOrder`, and "next untranslated slot in this
  entry". Without the param, the editor is byte-for-byte today's editor (deep links and the flat
  list keep working).
- Translation detail (API + editor page) lists memberships: kind, name, role, link back to the
  catalog entry page.

## Contract

- **Import:** `POST /api/v1/catalog-entries/import` — multipart zip + form field `datasetLabel`
  (Admin; `DisableAntiforgery` like the text import; size cap via settings, default 100 MB
  compressed). Response `CatalogImportSummary { DatasetLabel, PerKind: [{ Kind, Entries, Slots,
  MatchedSlots, DanglingSlots }], Warnings }`. Errors: 400 validation / 401 / 403 / 422
  malformed-or-empty (ProblemDetails).
- **List:** `GET /api/v1/catalog-entries?kind=Quest&category=&search=&progress=&sort=&page=&pageSize=`
  → `PaginationResponse<CatalogEntryListItemResponse>` where the item =
  `{ Did, Kind, Name, Category, Level, Progress { TotalSlots, Approved, Draft, NeedsReview,
  Untranslated, MissingSlots } }` + HATEOAS links.
- **Detail:** `GET /api/v1/catalog-entries/{did:int}` → `CatalogEntryDetailResponse { Did, Kind,
  Name, Category, Level, Progress, Slots: [ { SortOrder, Role, ObjectiveIndex?, FileId, GossipId,
  Translation?: { Id, Status, SourceText, TranslatedText } } ] }` (`Translation: null` =
  dangling). 404 unknown DID.
- **Categories:** `GET /api/v1/catalog-entries/categories?kind=Quest` → distinct category strings.
- **Stats:** `GET /api/v1/catalog-entries/stats` → coverage rows by kind+category (counts as in
  Progress).
- **Membership:** `GetTranslation` response gains
  `Memberships: [{ Did, Kind, Name, Role, ObjectiveIndex? }]` (empty list for uncategorized).
- **Frontend routes:** `/catalog` (browser), `/catalog/{did}` (entry incl. the bulk-approve
  form), editor `?entry=` context. All SSR-pure (forms + GET links only).
- **Files touched:** none outside the TMS. No `||` format change, no patcher change, no artifact
  schema change. New EF migration is **additive only** (three new tables + indexes) — trivially
  N-1 compatible (ADR-0023).

## Acceptance criteria

- [ ] Importing the golden fixture zip creates the expected entries + slots with correct roles,
      objective indexes, document order, and category names; the summary's matched/dangling
      counts equal the fixture's designed truth.
- [ ] The Bird & Baby fixture quest's slots resolve — via pure key join — to seeded translation
      rows (name, description, bestowal, objective, dialog, progress), proving the end-to-end
      join in an integration test.
- [ ] Re-importing the identical zip is a no-op state-wise (idempotent); importing a
      quests-only zip leaves deeds untouched (replace-per-kind).
- [ ] Malformed XML, malformed token, or zero-entry file → 422, database state intact
      (all-or-nothing).
- [ ] Catalog list: filters (kind/category/search/progress) and sorts return correct, totally
      ordered pages; progress counters match the seeded row statuses; a dangling slot never
      inflates a denominator.
- [ ] Catalog entry detail: slots in document order, dangling slots flagged, translation snippets
      and statuses correct; unknown DID → 404.
- [ ] Translation detail lists correct memberships; an uncategorized row returns an empty list.
- [ ] Entry-scoped bulk approve approves exactly the entry's approvable slots (existing endpoint
      semantics: skips report as skipped) and the artifact converges to include them
      (poll-with-timeout per ADR-0021 test idiom).
- [ ] Catalog import alone never schedules an artifact rebuild and never mutates any
      `Translations` row (byte-identical rows incl. timestamps).
- [ ] A registry-kind fixture (items) harvests exactly its token-bearing attributes as slots
      (description/pluralName), invents **no** Name slot, and lands under its own kind without
      touching quest/deed data (replace-per-kind proof across tiers).
- [ ] The frontend shows the LOTRO Companion credit (footer/about) once catalog pages ship.
- [ ] Authorization: import is Admin-only (403 for translator — the wrong-role matrix extends per
      AUDIT-TEST-02); reads are anonymous; SSR purity gate stays green with the new pages.
- [ ] E2E: upload fixture → browse `/catalog` → open the quest → translate two slots via the
      editor (with breadcrumb/next-slot nav) → bulk approve on the entry page → downloaded
      artifact contains the rows.

## Open questions

### Empirical — answered (sources: `docs/knowledge-base/lotro-companion-data-model.md`)

- *Can our rows be deterministically grouped into game objects?* **Yes.** Companion's XML
  carries `key:<FileId>:<GossipId>` on every translatable field; verified in their generator code
  (`I18nUtils.getKey`), in published data, and against our live 792,509-row export (Bird & Baby
  probe). Roles derive from XML structure.
- *Do we need their label files / their DAT parser?* **No.** English source comes from our own
  export; structure comes from the lore XML alone; their `lotro-dat-utils` is closed-source and
  irrelevant to us.
- *How fresh is their data and how do we get it?* The `lotro-data` GitHub repo is the live
  dataset, updated within days of each LOTRO patch (48.8 on 2026-07-01); raw-file fetch or repo
  zip. Items live in `lotro-items-db`.
- *What breaks on version skew?* Nothing — skew manifests as dangling slots (both directions),
  tolerated by design. `[needs live test]` only in the mild sense that each future game update
  re-confirms the dangling fraction stays small; the import summary surfaces it.
- *Token width?* Their tokens are Java `int`; our `GossipId` is `long` — superset, safe.

### Business decisions — resolved by the user, 2026-07-06

- **Q1 — Kinds: everything with tokens.** Every token-bearing Companion lore file becomes a
  kind. Realized as two tiers: curated quest/deed mappers first (the epic's core loop + DoD),
  then the harvester-registry long tail (items, NPCs, skills, traits, titles, emotes, factions,
  dungeons, mobs, effects, recipes, sets, …) as dedicated tickets **inside the same epic**.
  Schema and API are kind-generic from day 1; adding a kind is a registry row + fixture.
- **Q2 — Acquisition: admin uploads a zip** (same trust/offline model as `exported.txt`).
  "Fetch latest from GitHub `lotro-data`" = named fast-follow after the epic.
- **Q3 — Progress: the bar counts Approved only** ("done" = shippable in the artifact);
  Draft/NeedsReview are secondary counters.
- **Q4 — Attribution: visible UI credit + contact the maintainer.** Footer/about credit ships
  with the frontend tickets; reaching out (Discord/forum — also seeding the future `labels/pl`
  contribution) is Artur's checklist item on the epic issue.
- **Q5 — Anonymous browsing: yes** — catalog browser/detail/stats are public read-only, exactly
  like `/translations`; writes stay role-gated.
- **Naming (amendment, same day):** the layer's concept is **CatalogEntry** (tables
  `CatalogEntries`/`CatalogTextSlots`, routes `/api/v1/catalog-entries` + `/catalog`) — the word
  "entity" is banned in this layer to avoid the DDD-Entity misconception (this data is
  deliberately *not* domain-modeled; see the M7-01 ADR).

## Assumptions

- One current Companion dataset at a time (per kind); no per-version catalog snapshots.
- Import cadence ≈ once per game update, admin-driven, single-flight; volumes (≈20k entries /
  ≈220k slots for quests+deeds; estimated ≈250k entries / ≈300–500k slots at full registry
  coverage) sit comfortably in the COPY-import envelope proven at 792k rows.
- Companion's XML schema evolves additively in practice; the importer whitelists what it reads
  and warns rather than breaks on novelty inside known files.
- The existing editor, upsert, approve, bulk-approve, artifact and distribution slices remain the
  single write path for translation content.
- DIDs fit positive `int` by construction (0x70-band ends at `int.MaxValue`).

## Impact on existing docs & backlog (applied 2026-07-06)

- **Tickets:** epic **M7** cut (#362 tracking + #363–#375, dependency-ordered); #30 closed as
  superseded-by-M7 (its `key:{file_id}:{gossip_id}` premise is now proven and absorbed); #38
  re-scoped to its glossary half (quest browser moved to M7).
- **CLAUDE.md:** Roadmap gains M7 (game-content catalog); read-first routing points at spec 0008
  + the knowledge-base doc for anything touching the catalog layer.
- **ADR:** new ADR at M7-01 start — "Game-content catalog is imported reference data (persistence
  models + read models + COPY port; no domain aggregate)" with the per-kind-replace decision and
  the no-"entity" naming rule.
- **Knowledge base:** `lotro-companion-data-model.md` committed (2026-07-06).

## Epic cut (M7 — GitHub issues #362–#375)

| # | Issue | Ticket | Depends on | Scope |
|---|---|---|---|---|
| M7-00 | #362 | [Epic] tracking issue | — | DoD = the E2E criterion of this spec |
| M7-01 | #363 | ADR + reference-data model: tables, migration, read models, indexes | — | `CatalogEntries`, `CatalogTextSlots` (+ `(FileId, GossipId)` index), `CompanionDatasets` |
| M7-02 | #364 | Companion parsing core + quest mapper + golden fixtures | #363 | token parser, streaming `XmlReader` walker, role taxonomy, Bird & Baby + argful + multi-objective fixtures |
| M7-03 | #365 | Deed mapper + enum/category resolution | #364 | deeds.xml, `QuestCategory`/`DeedCategory` enum + label files, category fallback |
| M7-04 | #366 | Import slice: zip intake → replace-per-kind → COPY → summary | #364, #365 | guards (422s), idempotence, dangling stats, integration tests, wrong-role 403 |
| M7-05 | #367 | Catalog list + categories endpoints (filters, paging, progress) | #363 | read models, aggregation, HATEOAS, integration tests |
| M7-06 | #368 | Catalog entry detail + translation memberships | #363 | ordered slots, dangling flags, `GetTranslation.Memberships`, tests |
| M7-07 | #369 | FE: catalog browser `/catalog` + Companion credit | #367 | SSR list, filters, progress bars |
| M7-08 | #370 | FE: catalog entry `/catalog/{did}` + entry bulk approve | #368 | slots by role, status badges, editor links, bulk-approve form |
| M7-09 | #371 | FE: editor catalog context | #368 | breadcrumb, prev/next, next-untranslated |
| M7-10 | #372 | Coverage stats endpoint + dashboard widget | #367 | by kind+category |
| M7-11 | #373 | E2E: atomic quest translation loop | #366, #369–#371 | Playwright scenario per the E2E acceptance criterion |
| M7-12 | #374 | Harvester registry + items kind | #366 | generic token harvester over registry config; `items.xml` (149,710 records — descriptions/pluralName, **no Name slot**); fixture + integration tests |
| M7-13 | #375 | Long-tail kinds via registry | #374 | NPCs, skills, traits, titles, emotes, factions, dungeons, mobs, effects, recipes, sets, … — one registry row + fixture each; kind dropdown fed from data |
