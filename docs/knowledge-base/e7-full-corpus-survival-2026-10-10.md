# E7 — full-corpus revert rate and the source guard on a real update (2026-10-10)

**Status:** done, #1094 · **Box:** the owner's second Windows PC (`adminpc`) · **Tool:**
`scripts/experiments/e7-corpus-survival.ps1` · **Related:** spec 0012 (Business context §1, the
"Invariant" acceptance criterion), ADR-0047 / #659 (source guard), E5 and E6 in
[update-49/RESULTS.md](update-49/RESULTS.md).

## Why

Two numbers in spec 0012 had never been measured on a real update:

1. **The revert rate of a fully translated corpus.** The 9 live tests only ever had 8 resident
   translations in the DAT. The "1.52% per major" for U49 was an estimate from per-SubFile
   metadata.
2. **The source guard on a real update.** ADR-0047 says a re-patch with the pre-update file
   repairs every collateral revert and never puts Polish over changed English. That was proven on
   fixtures only.

## Method — forced downgrade with a marked corpus

The box had a backup of its DAT from before a real cumulative update **47.2 → 49.7** (two majors
plus point releases; see E6). That backup makes the update repeatable:

1. Export the 47.2 backup (788,271 rows in 277,084 text files, 7 columns with `source_digest`).
2. Build a corpus where every row is `[PL] ` + its own English, `approved = 1`, digest kept. No
   English row in 47.2 or 49.7 starts with `[PL]`, so the marker cannot occur naturally.
3. Patch a **copy** of the 47.2 DAT with it: 788,271 / 788,271 written in 10.2 s. E5 on the copy:
   **iteration unchanged in every SubFile**, so for the launcher it is still plain 47.2. Size
   changed in 275,545 text SubFiles, which is our own write.
4. Elevated: copy the patched DAT over the live DAT and start the launcher. It applied 294 English
   iterations (37.4 MB), the same delta as the unmarked replays in E6.
5. Export the result and classify every corpus row against a clean 49.7 export: marker present,
   English with the same source (collateral revert), English that SSG changed, or row removed.
6. Patch a copy of the updated DAT **again** with the same corpus and its ledger, export it, and
   classify again (the guard test).
7. Elevated: copy the clean 49.7 DAT back. SHA256 matches the backup, E5 diff against the state
   before E7 is 0. The live game ends clean.

## Results

### After the update

| Corpus rows (788,271) | Count | Share |
|---|---|---|
| Polish survived (marker present) | 764,842 | 97.03% |
| English, unchanged source (**collateral revert**) | 20,228 | 2.57% |
| English, **source changed by SSG** | 2,347 | 0.30% |
| Row removed by the update | 854 | 0.11% |
| Polish over changed English, anomalies | 0 | 0 |

- **2.97% of the rows lost their Polish**, in **1,661 SubFiles** (0.6% of the text SubFiles).
  **86% of the loss is collateral**: the English did not change, only the SubFile was replaced.
- **Survival is per SubFile, now counted on the whole corpus.** None of the 22,598 corpus rows in
  replaced SubFiles survived, and no row outside them was lost.
- **The E5 signal covers every loss.** All 1,661 SubFiles with a lost row are in the E5 diff of
  the same update: 1,188 with a moved iteration and 473 removed. None was missed. The E5 diff of
  the marked DAT matches the clean update exactly (1,223 SubFiles / 1,188 text), so our patch does
  not change what SSG replaces.

### After the guarded re-patch (ADR-0047)

Patcher summary: **785,070 applied**, **2,347 `source moved`**, 3,201 skipped (2,347 + 854
removed), 0 rows without a digest, 9.5 s.

| Corpus rows | Count |
|---|---|
| Polish (marker present) | **785,070** = 764,842 survived + 20,228 repaired |
| English, source changed by SSG | **2,347**, all skipped as `source moved` |
| Removed | 854 |
| Collateral revert left unrepaired | **0** |
| Polish over changed English (masking) | **0** |

The 18,703 rows that are new in 49.7 stay untouched English. **The guard holds on a real update:**
every collateral revert is repaired offline, with no TMS work. Every row whose English changed
stays English until someone translates it again. After the repair, only **0.41%** of the corpus
is English (changed or removed).

### Two side facts

- **60 of the 2,347 changes are letter case only** (the English differs from 47.2 only in upper
  and lower case). The guard counts them as changed English, which is correct under the invariant.
  The first PowerShell port of the classifier compared without case (`-eq`) and called them
  collateral reverts. The tool now compares with `-ceq`/`-cne`, and its counts match an
  independent Python run and the patcher's own summary exactly.
- **Our own patch moves size, never iteration**, here in 275,545 SubFiles. A size-based "was the
  DAT changed by SSG?" check would fire after every patch of ours. This is one more reason why
  Tier 0 (#565) keys on iteration.

## Limits

- **One write path: the CLI `patch`.** Tier 0 and Tier 1 (#565, #566) are not built yet. The spec's
  invariant criterion asks for every write path.
- **The corpus is 100% translated**, so 2.97% is an upper bound. The real loss is this rate times
  the share that is actually translated.
- **One cumulative update** (47.2 → 49.7, two majors in one go), replayed by forced downgrade. A
  single major should lose less. The Wolves of Mordor expansion (2026-10-28) is the next real
  single-major measurement, with the same procedure.

## Files (gitignored `intel/update-49.7/`)

`export-47.2.txt` (pre-update export) · `export-49.7.txt` (clean post-update export) ·
`e7/export-e7-patched-47.2.txt`, `e7/export-e7-after-update.txt`, `e7/export-e7-after-repatch.txt`
· E5 snapshots `e7/e5-e7-*` · `e7/translations/e7pl.txt` + its ledger · `e7/e7-driver.log` and
`e7/e7-driver.ps1` (the elevated helper that swapped the DAT and ran the launcher) · `e7/e7.py`
(the independent Python cross-check). The two DAT working copies were deleted after the run. They
can be rebuilt from `client_local_English.pre-update-2026-04-18.dat` in about a minute.
