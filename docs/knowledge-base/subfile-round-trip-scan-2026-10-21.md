# Every subfile of the live English DAT round-trips byte for byte (2026-10-21)

**Status:** settled for the DAT measured. The scan program was a one-off and is not in the repo. For a
later DAT, a `patch` run warns about a subfile it refuses to write, but only when an approved row with
a source digest points at that subfile, and once per such row until #1239 lands. `export` reads such
a subfile as before and never refuses it.

## Question

When the patcher writes Polish into a subfile, it parses the subfile and builds it again from the
parts it understood (#1194). Some shapes make that rebuild lose data or change bytes: a repeated
fragment id, bytes after the last declared fragment, a VarLen value below 128 stored in the two-byte
form, UTF-16 text with an unpaired surrogate, a non-text subfile, and a fragment order the rebuild
does not keep. Does the real game data hold any of them?

## Method

- The DAT: `client_local_English.dat` from the Program Files install on the owner's Windows PC, as
  the launcher left it on 2026-10-21 (1,910,505,472 bytes). It held no Polish: no fragment contained
  a Polish letter, so every subfile was SSG's own.
- Opened read-only through `DatFileHandler` (flag `0x4`, see
  [datexport-readonly-open-2026-08-07.md](datexport-readonly-open-2026-08-07.md)). The game was not
  running.
- A throwaway x86 console program read every subfile. For each text subfile it walked the header
  and the declared fragments by hand, counting repeated ids, ids out of ascending order, and the
  bytes left after the last fragment. Then it ran `SubFile.Parse` + `Serialize` and compared the
  result with the bytes read. That comparison is exactly the check `LoadSubFile` now makes by
  default, before the patcher may write (export opts out with `readOnly: true`). For each non-text
  subfile it ran the same round trip. One pass takes about 30 seconds.
- The scan ran on the code before #1194 and again on the final #1194 code, with the same result.

## Result

| | Count |
|---|---|
| Subfiles | 311,970 |
| Text subfiles (`0x25`) | 282,190, of them 1,584 with no fragments |
| Fragments | 806,120 |
| Non-text subfiles | 29,780 (`0x0A` 29,197, `0x22` 302, `0x41` 223, `0x42` 45, 13 others) |
| Repeated fragment ids | 0 |
| Subfiles with bytes after the last fragment | 0 |
| Text subfiles that fail to parse | 0 |
| Text round-trip mismatches = subfiles the #1194 check refuses | 0 |
| Non-text round-trip mismatches (#1194 code) | 0 |
| Text subfiles whose fragment ids are **not** in ascending order | **129,165** (46%) |

## What it means

- None of the risky shapes occur in today's data, so the #1194 check refuses nothing in the current
  game. It only stops a future update from losing data silently.
- File order is not id order in almost half of the text subfiles. Any code that rebuilds a subfile
  must keep the order it read. Sorting by id would rewrite 129,165 subfiles. This is why
  `SubFile.Fragments` is an `OrderedDictionary`.
