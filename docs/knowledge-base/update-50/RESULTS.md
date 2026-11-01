# Update 50 "The Wolves of Mordor" — measured on a real update (2026-10-29)

**Status:** done · **Box:** the owner's gaming PC (Windows 11), sandbox copy of the install ·
**Operator:** Claude, alone · **Before:** [BASELINE.md](BASELINE.md) (49.8) · **Related:**
spec 0012 (Tier 0, the "Invariant" acceptance criterion), ADR-0047 (source guard), E5 and E6 in
[../update-49/RESULTS.md](../update-49/RESULTS.md), E7 in
[../e7-full-corpus-survival-2026-10-10.md](../e7-full-corpus-survival-2026-10-10.md), #565 (Tier 0)

## Why

E7 measured one cumulative update (47.2 → 49.7, two majors at once) and named this expansion as
the next real **single** major. Update 50 is also the first real update since Tier 0 (#565) was
built, so the `launch` path could be measured on it, not only the CLI `patch`.

## Method

Everything ran in the sandbox copy of the install (`%USERPROFILE%\lotro-sandbox\LOTRO`, see
BASELINE.md). The launcher patches it with no UAC prompt, and the live install was never touched:
its English DAT still has the 49.8 hash `0F8C7BB1…3F4` after the run.

1. **The real update, clean.** Started the sandbox launcher on clean 49.8 under the capture script
   (`intel/update-50/e50-capture.ps1`). Then an E5 snapshot and an export of the result.
2. **E7 with the marked corpus.** Put the marked 49.8 DAT from the baseline into the sandbox
   (forced downgrade of the English DAT only, as in E7) and ran our `launch` from a folder that
   holds the corpus, its ledger and its Tier 0 snapshot. The launcher then applied Update 50 to
   the marked DAT. Export, then classify every corpus row against the clean 50 export.
3. **Tier 0 repair through `launch`.** Ran `launch` again: Tier 0 compares the snapshot with the
   DAT and re-patches. Then started the launcher once, as a player would, exported and classified
   again.
4. **The next launch.** Ran `launch` a third time to check that Tier 0 finds nothing after the
   repair and after the launcher's own start.
5. Put the clean 50 DAT back into the sandbox, so it is ready for the next update.

`launch` refuses to start the launcher, because SSG's current launcher has no signature (#1290,
see "Side findings"). Tier 0 runs before that step, so the measurement was not affected. The
launcher was started by hand each time instead.

The CLI was built from `main` at `c710a3ad`.

## Results

### The real update (49.8 → 50, clean)

| | 49.8 | 50 | Change |
|---|---|---|---|
| Iterations applied (all DAT files) | | **7,711**, 1,480,147,543 B downloaded, 101 s | |
| English iterations | | **731** (ids 26880–27610) | E7 had 294 for two majors |
| English DAT size | 1,910,505,472 | 1,989,165,432 | +78,659,960 B (75.02 MiB) |
| English DAT SHA256 | `0F8C7BB1…3F4` | `D8A99B4C…F65` | |
| vnum | 112 / 3 | unchanged | none: E5 reads the same raw values before and after (it prints the second one as 50,528,256, which is 0x03030000) |
| Subfiles (all / text) | 311,970 / 282,190 | 318,058 / 286,762 | +6,088 / +4,572 |
| Export: rows | 806,120 | 822,907 | +16,787 |
| Export: text files with rows | 280,606 | 285,108 | 4,575 new, 73 gone |

- **Rows:** 16,949 added, 162 removed, **7,026 changed** (79 of them in letter case only). The
  `source_digest` column moved for exactly those 7,026 rows.
- **The E5 signal matches the change exactly.** 811 text subfiles have a new iteration (738) or
  are gone (73). All 811 text files of 49.8 that gained, lost or changed a row are in that set,
  none is outside it, and no text subfile in it is unchanged. The size moved in 569 text subfiles
  (582 in all), always together with the iteration.
- **The English DAT is written only at the end.** The launcher writes the other DAT files during
  the whole 101 s, but the English DAT only in the last 4.3 s (22:40:27.2 → 22:40:31.5).
- The launcher first updated itself and the two client programs (3 file patches, 20 MB), then
  restarted. No UAC prompt at any point.

### E7: the marked corpus through the real update

The launcher applied the same 731 English iterations (84,268,739 B) to the marked DAT. The E5 diff
of the marked DAT gives the same 811 text subfiles as the clean update, so our patch does not
change what SSG replaces.

| Corpus rows (806,120) | Count | Share | E7 (47.2 → 49.7) |
|---|---|---|---|
| Polish survived (marker present) | 787,086 | **97.64%** | 97.03% |
| English, unchanged source (**collateral revert**) | 11,846 | 1.47% | 2.57% |
| English, **source changed by SSG** | 7,026 | 0.87% | 0.30% |
| Row removed by the update | 162 | 0.02% | 0.11% |
| Polish over changed English, anomalies | 0 | 0 | 0 |

- **2.36% of the rows lost their Polish, in 811 subfiles** (0.29% of the text files). 62% of the
  loss is collateral (E7: 86%). This single expansion changed more English than the two majors in
  E7 together (7,026 rows against 2,347), but replaced fewer subfiles (811 against 1,661).
- **Survival is per subfile again.** Every lost row is in one of the 811 subfiles of the E5 set, and
  no marked row survived inside them.
- None of the 16,949 new rows carries the marker.
- The Python classifier and `e7-corpus-survival.ps1 -Classify` give the same counts
  (`e7/classify-*-after-update.txt`).

### Tier 0 repair through `launch` (ADR-0047 on the `launch` write path)

| Launch | Tier 0 says | Result |
|---|---|---|
| 1, on the marked 49.8 DAT before the update | "all 282,190 text subfiles are as our last patch left them" | nothing to do |
| 2, after the update | "a game update replaced 811 text subfile(s) (738 with a new iteration, 73 removed)" | **798,932 applied, 7,188 skipped**; detection 0.2 s, repair 6.7 s |
| 3, after the repair and one launcher start | "all 286,762 text subfiles are as our last patch left them" | nothing to do |

- **811 found = the E5 set.** Tier 0 found every replaced subfile and nothing else.
- **798,932 = 787,086 survivors + 11,846 collateral reverts.** 7,188 skipped = 7,026
  `source moved` + 162 rows whose subfile is gone.
- After the repair and a launcher start, the corpus is:

| Corpus rows (806,120) | Count | Share |
|---|---|---|
| Polish (marker present) | **798,932** | 99.11% |
| English, source changed by SSG (skipped as `source moved`) | 7,026 | 0.87% |
| Removed | 162 | 0.02% |
| Collateral revert left unrepaired | **0** | |
| Polish over changed English (masking) | **0** | |

- **The launcher start keeps the repair.** It applied 0 iterations and did its every-start write
  (the DAT mtime moved, the size did not). The third launch found nothing, so that write does not
  look like an update to Tier 0.
- The repair grew the DAT by exactly 1 MiB (1,996,526,576 → 1,997,575,152 B).

## Side findings

- **SSG's launcher has no signature any more (#1290).** `LotroLauncher.exe` 49.8
  (4908.0071.2801.4041) and 50 (5000.0071.3771.4007) are both "NotSigned". The client programs are
  still signed by "Standing Stone Games LLC". The installer ships an old signed launcher (2019).
  The first self-update moved it into `backup\`, and the next one replaces it: after Update 50 the
  sandbox's `backup\` holds the unsigned 49.8 one. Our `launch` checks the signature
  (AUDIT-SEC-02), so today it patches and repairs, and then refuses to start the game with exit
  code 3.
- **The E5 script could not find `datexport.dll` on its own.** Windows PowerShell 5.1 leaves
  `$PSScriptRoot` empty in `param()` defaults when a script is started with `-File`. E5, E1 and E2
  now resolve their default paths in the script body, as E6 already did.
- The polls on 2026-10-28 (04:27–11:36) found no data patch. SSG's maintenance window was
  14:00–18:00 Polish time that day.

## Limits

- **Two write paths of three.** The guard is now proven on the CLI `patch` (E7) and on the Tier 0
  repair in `launch` (here). Tier 1 (#566) is not built.
- **Forced downgrade of one file.** For the marked run, only the English DAT was set back to 49.8;
  the rest of the sandbox was already on 50. E6 and E7 showed that the launcher applies the same
  English delta this way.
- **The corpus is 100% translated**, so 2.36% is an upper bound, as in E7.

## Files (gitignored `intel/update-50/`)

`export-49.8.txt`, `export-50.txt` · E5 snapshots `e5-pre-u50-49.8-*`, `e5-post-u50-clean-*`, and
`e5set-u50.txt` (the 824 subfile ids of any kind with a new iteration or removed; 811 of them are text) ·
`client_local_English.pre-u50.dat` and `client_local_English.u50-clean.dat` · `e7/`: the marked
49.8 DAT, `export-e7-after-update.txt`, `export-e7-after-tier0-repair.txt`, the E5 snapshots of the
marked DAT and the `launch` console logs · `tier0/`: the corpus, its ledger and snapshot, and
`data/patcher.log` with all three launches · `capture/`: timeline, file-watcher and launcher logs
of every launcher start · `tools/`: the Python classifier, export diff, E5 diff and the
iteration counter.
