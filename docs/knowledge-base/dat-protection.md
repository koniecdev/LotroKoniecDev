---
name: DAT Protection NOT NEEDED — translations survive all updates (including major)
description: attrib +R protection is unnecessary. Launcher uses chunk-based patching. Translations survive updates without any protection — with one refined limit: survival is per-SubFile (an update that modifies a SubFile reverts our fragments inside it; first observed 48.8→49.1). Validated across 9 live tests including majors 48.0 (2026-04-23) and 49 (2026-08-02).
type: project
originSessionId: fcf33c21-a957-445f-bc57-c8ea7814f1b6
---
# DAT Protection Status: NOT NEEDED

> **Addendum (2026-08-02) — the survival model is per-SubFile, not per-fragment.** 9th test
> (major 48.8 → 49.1, "In Good Company"): **7/8 survived byte-for-byte; 1/8 REVERTED** to its
> English original because SSG added 4 fragments to its SubFile (620757435: 1019→1023) — the
> launcher replaced that whole chunk, wiping our fragment even though its own text was unchanged.
> Counter-proof: the four SubFiles holding the surviving pairs kept identical fragment counts.
> **The "protection NOT needed" conclusion stands** — `attrib +R` cannot save a chunk the update
> legitimately replaces; the repair is a normal re-patch (and in the TMS model, exactly the
> spec-0001 invalidation loop — its first real-world case). See
> [live-test-2026-08-02.md](live-test-2026-08-02.md) + [update-49/RESULTS.md](update-49/RESULTS.md).

> **Addendum (2026-07-11):** an 8th confirmation — resident translations survived the 48.7→48.8
> cycle untouched (SKIP path, hash match) and a forced live `patch` (8/8, 0 warnings) confirmed
> the WRITE path on the 48.8 DAT. See [live-test-2026-07-11.md](live-test-2026-07-11.md).

## The original assumption (WRONG)

We assumed LOTRO's official launcher overwrites DAT files on update, wiping translations. This drove the entire Legacy launch flow with attrib +R protection, process monitoring, and game client killing. **Wrong.**

## Empirical evidence: 9 independent tests

| Date | Update type | DAT modified? | Result |
|------|-------------|---------------|--------|
| 2026-03-02 | 47 major (second PC) | Possibly | ✅ translations survived |
| 2026-03-16 | 47 → 47.1 | No | ✅ survived |
| 2026-03-17 | chunk patches | Yes (minor) | ✅ survived |
| 2026-03-22 | 47.1 → 47.2 (chunk patch) | Yes (actively patched) | ✅ 8/8 survived, visual + export |
| 2026-03-22 #2 | SKIP path verification | No | ✅ 255ms launch |
| **2026-04-23** | **47.2 → 48.0 (MAJOR)** | **Yes (+14MB, +4,231 fragments)** | **✅ 8/8 survived, 4 channels verified** |
| **2026-06-25** | **48.0 → 48.7 (point, SKIP path)** | **Yes (+1 MiB, +1,159 fragments)** | **✅ 8/8 survived, 4 channels, byte-identical** |
| 2026-07-11 | 48.7 → 48.8 (point) | Yes (minor) | ✅ 8/8 survived + forced live WRITE-path re-patch OK |
| **2026-08-02** | **48.8 → 49.1 (MAJOR)** | **Yes (+1 MiB, +7,192 fragments, in-place chunk rewrites)** | **⚠️ 7/8 survived byte-for-byte; 1/8 reverted — its SubFile was modified (+4 fragments) → whole chunk replaced** |

## How translations survive: chunk-based patching

1. LOTRO launcher downloads partial patches (e.g. `client_local_English-<version>.dat`) containing only changed entries.
2. Launcher applies those chunks, overwriting only specific offsets in `client_local_English.dat`.
3. Our translated fragments sit in **untouched chunks** — launcher never touches them during update.
4. Result: translation bytes stay intact byte-for-byte.

**48.0 verification:** 587 diff hunks between 47.2 and 48.0 exports, **0 polish matches in the diff** (translations were stable bytes). 8/8 entries exist in both exports unchanged.

**48.7 verification:** 204 diff hunks between 48.0 and 48.7 exports, **0 polish matches in the diff**. 8/8 byte-identical. The live DAT just before 48.7 had the **same SHA256** as the post-48.0 DAT (`833C22DE…1826`) — translations sat untouched across the entire 48.x cycle (Apr→Jun) with zero re-patch. The DAT grew by **exactly +1 MiB** (one allocation block) for the new content — direct evidence of block-granular allocation behind chunk-based patching.

## What we used to do (DEPRECATED)

- `attrib +R` read-only flag on DAT before launch, `-R` after — **unnecessary and harmful**
- Process monitoring with `WaitForLauncherCompletionAsync` — **unnecessary, killed game sessions**
- Vnum-triggered re-patch — **would never fire (vnum never changes) AND would be destructive if it did**
- Forum scrape as update trigger in patcher — **moved to M3-11 API cron for admin notification only**

## Current approach (simplified flow — PRODUCTION READY)

```
1. Hash polish.txt → compare to stored hash
2. If changed: apply translations, save new hash + current vnum snapshot
3. Fire-and-forget launcher → it handles its own update if any
4. Done. No protection, no monitoring, no killing processes.
```

**Across 9 live tests (two majors) the only loss is the per-SubFile revert above — never a flow failure.** datexport.dll READ + WRITE paths confirmed compatible with 48.0, 48.7, 48.8 and 49.1 schemas (vnum 112/3 throughout).

## Russian project comparison

- **Russians use:** `-disablePatch` flag (undocumented, SSG can remove anytime)
- **We used:** `attrib +R` (OS-level block)
- **Reality:** Neither is needed — translations survive updates anyway
- Russians also have NinjaMark (marker in DAT subfile 620750000) to detect launcher overwrites —
  re-evaluated 2026-08-02 after the per-SubFile revert finding: the **problem class is real**
  (our old "non-problem" verdict is retired), but the in-DAT marker is the wrong implementation
  for us: it only fires when ITS OWN chunk is wiped (misses collateral wipes elsewhere), reading
  it needs elevated DAT access on every launch (datexport opens with write intent), and our
  lost-state edge already degrades gracefully (missing version file ⇒ hash=null ⇒ full re-patch).
  An external DAT fingerprint (size+mtime in the version file) detects strictly more, with zero
  DAT access on the SKIP path — see `update-49/RESULTS.md` §scenariusze (launch sentinel)

## Cross-reference

- [live-test-2026-03-16.md](live-test-2026-03-16.md) — earlier chunk-patch tests
- [live-test-2026-04-23.md](live-test-2026-04-23.md) — major update 48.0 test
- [update-detection-strategy.md](update-detection-strategy.md) — why hash is the only valid trigger
- [vnum-observations.md](vnum-observations.md) — why vnum can't be used as trigger
