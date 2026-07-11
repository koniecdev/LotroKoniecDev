---
name: Game update detection and translation versioning strategy
description: How the system detects LOTRO updates, validates translations against game versions, and manages the re-patching lifecycle — two-source confirmation model
type: project
---

> **Superseded in part (2026-06-11):** the vnum-based "Docelowy Model" (`CompatibleSinceVnum`,
> vnum-filtered export) and the two-source crowdsource+forum detection below are superseded by
> [`docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md`](../specs/0001-game-update-lifecycle-and-translation-invalidation.md)
> — forum-only detection, `GameVersion` aggregate, status-based invalidation (`NeedsReview` +
> `PreviousSourceText`), pre-built ETag-cached distribution artifact, CLI auto-download (M2-20).
> The Core Insight below and the admin upload→diff flow remain valid and are codified there;
> crowdsourced reports stay post-MVP (#84/#31).
>
> **Update (2026-07-11, ADR-0030):** the "Admin flow after confirmed update" stays the one fully
> manual link — the VM/unattended export runner is deferred with named reconsider triggers, the
> forum watcher (#85, still open) gains an e-mail alert, and the export read path is de-elevated.
> See [`docs/adr/0030-game-version-export-stays-manual-vm-runner-deferred.md`](../adr/0030-game-version-export-stays-manual-vm-runner-deferred.md).

## Core Insight: Re-patching After Game Update is WRONG

When SSG releases an update that changes existing English texts (e.g. lore corrections, quest rewrites), blindly re-applying old translations would overwrite fresh English with stale Polish. Translations must be validated against the new game version before re-application.

**Why:** A translation is only valid for the game version it was created against. If SSG changed the source text, the translation is stale and must be re-reviewed.

**How to apply:** Never trigger re-patch based on vnum change alone. The only valid local trigger is "translation file changed" (meaning: translators have released a new, version-aware patch).

## Docelowy Model: API as Source of Truth

```
TranslationEntity:
  FileId, GossipId, LanguageCode, Content, ArgsOrder
  Status: Draft | Submitted | Approved | NeedsReview
  CompatibleSinceVnum: int  // "this translation is valid for vnum >= X"

API endpoint:
  GET /api/v1/translations/export?lang=pl&vnum=115
  → returns ONLY translations where Status=Approved AND CompatibleSinceVnum <= 115
```

Patcher on user's PC:
1. Read vnum from DAT
2. Download translation file from API (filtered by vnum compatibility)
3. Compare hash with last applied → if different, patch
4. Fire-and-forget launch

## Two-Source Update Detection (Crowdsource + Forum)

Neither source alone is sufficient:

| User vnum report | Forum post | Result |
|---|---|---|
| Yes | Yes | **Confirmed** → notify admin |
| Yes | No | Possibly fake, or forum delayed → wait |
| No | Yes | We know update exists but no vnum yet → wait for user |
| No | No | Nothing happening |

### End-user flow:
```
User launches patcher → reads vnum from DAT → POST /api/v1/game-version/report { vnum }
→ API compares with stored → if newer, triggers forum check
→ Both sources agree → Confirmed = true → Discord/email notification to admin
```

### Admin flow after confirmed update:
```
1. Admin updates own LOTRO installation
2. Runs: dotnet run -- export → exported_v115.txt
3. Runs: dotnet run -- import-exported-texts exported_v115.txt
4. DB diffs: new EnglishContent vs old → changed texts' translations marked "NeedsReview"
5. Translators handle NeedsReview texts in web UI
6. Approved translations become available via API export
7. Next user patcher run downloads new file, detects hash change, patches
```

### Forum cron — standalone value for admin:
Even without user vnum reports, a periodic forum check (cron or manual) gives the admin a heads-up that an update dropped. Useful because the admin needs to update their own LOTRO and re-export.

## GameVersionReport Domain Model

```
GameVersionReport:
  VnumDatFile: int
  VnumGameData: int
  FirstReportedAt: DateTime
  ReporterCount: int          // how many users reported this vnum
  ForumVersion: string?       // from scraping, e.g. "48.0"
  ForumDetectedAt: DateTime?
  Confirmed: bool             // true when BOTH: vnum report + forum post
```

## Current State (MVP — Simplified Flow)

Simplified flow with hash-based trigger is CORRECT for the local-file phase:
- Translation file hash changed → patch → launch
- Translation file unchanged → skip → launch
- Game updated but file unchanged → skip (CORRECT — stale translations shouldn't overwrite fresh English)

This naturally evolves into the API model: instead of local file hash, the patcher downloads from API and compares hash.

## Why Vnum-Triggered Re-Patch Was Almost Implemented (and Why It's Wrong)

After live tests showed translations survive updates, we considered adding vnum comparison as a second re-patch trigger. This would have been harmful:
- `if (translationChanged || datUpdated) { repatch }` ← WRONG
- Would blindly re-apply stale translations over fresh SSG English texts
- The correct response to a game update is: wait for translators to validate, not auto-re-patch
