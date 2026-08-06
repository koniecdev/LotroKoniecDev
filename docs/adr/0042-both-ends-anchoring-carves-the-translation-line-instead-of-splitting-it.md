# ADR-0042: The `||` line is carved by both-ends anchoring, not by `Split` — and the separator stays unescaped

**Status:** Accepted
**Date:** 2026-08-06
**Decision-makers:** Solo maintainer
**Related:** #597 (the defect), #569 (the hostile-string coverage that found it), ADR-0039 (the content escape, which deferred this), CLAUDE.md "Translation file format — THE inter-context contract", `TranslationFileParser`, `TranslationExportParser`

## Context

The `||` file is the only contract between the two bounded contexts:

```
file_id||gossip_id||content||args_order||args_id||approved
```

Content may legally contain `||`, so neither parser can simply take the third field. Both were
written to "anchor from both ends": the first two fields lead, the last three trail, and everything
between is re-joined as content. Both implement it the same way — `line.Split("||")`, then index
`parts[2..^3]`.

That is not both-ends anchoring. `Split` resolves **every** boundary greedily left to right
*before* the anchoring happens, so a boundary inside a pipe run is already decided by the time the
indexes are applied. Content ending in an odd run of `|` therefore merges with the separator that
follows it and the content boundary lands one character too early.

Content `abc|` is written as `620756992||1001||abc|||NULL||NULL||1` and splits into:

```
["620756992", "1001", "abc", "|NULL", "NULL", "1"]
```

Measured, before this ADR — leading and interior pipes were already fine, only an odd **trailing**
run broke:

| content written | content read back | args_order read back |
|---|---|---|
| `abc\|` | `abc` | `\|NULL` |
| `abc\|\|\|` | `abc\|\|` | `\|NULL` |
| `\|` | (empty) | `\|NULL` |
| `\|abc` | `\|abc` | `NULL` |
| `abc\|\|` | `abc\|\|` | `NULL` |
| `a\|b` | `a\|b` | `NULL` |

Both parsers did it identically, which is why `ParserContractParityTests` — the drift guard — could
not see it: the two contexts agreed on the wrong answer.

The corrupted args column then diverged between the contexts, which is worse than a plain
truncation. The patcher's `ParseArgsArray` reached `int.Parse` inside a bare `catch { return null; }`
and dropped the arguments silently, so a fragment that had reordered arguments lost that ordering.
The TMS stored `"|NULL"` verbatim as `ArgsOrder` — neither `null` nor a valid `1-2-3` column — and
because `TranslationSource` equality covers the args columns (spec 0001), that value also
participated in the import diff.

Reachable end to end: a translator types a trailing `|` in the editor, the artifact is written, the
CLI downloads it, and the wrong text is patched into the DAT with no warning to anyone.

ADR-0039 settled the *content* escape (`\`→`\\`, CR→`\r`, LF→`\n`) and deliberately left the
separator unescaped, naming this defect as out of scope. This ADR closes that item.

## Decision

### 1. The separator stays unescaped — the information was never lost

The format is **not** ambiguous, and it never was. The five non-content fields cannot contain a
`|`: the two id columns are ASCII decimal integers, the two args columns are `NULL` or a
`-`-separated integer list, and the approved column is `0` or `1`. So at a field boundary next to
content, a run of `n` pipes is always `content_pipes + 2`, and the separator is:

- the **first two** characters of the run at the leading boundary, and
- the **last two** characters of the run at the trailing boundary.

Both are recoverable by construction. The bytes on disk always carried the right answer; the
parsers threw it away.

Escaping the separator was rejected. It is a format change: it would break every `exported.txt` and
every `polish.txt` in existence, force a version gate between the CLI and the TMS, and rewrite the
golden fixtures — all to encode information the line already carries. ADR-0039 declined it for the
same reason and nothing here changes that.

### 2. Both parsers carve the line by scanning, not by splitting

The two leading separators are found by a **forward** search from the start of the line; the three
trailing separators by a **backward** search, each over the slice that ends where the previously
found separator begins. Content is the substring between the second leading separator and the
third-from-last one.

Slicing before searching is what makes the backward pass exact. A match must fit entirely inside
the remaining slice, so when an args column is empty the separator pair that straddles the slice
boundary cannot be mistaken for the next separator.

That one detail is load-bearing and invisible in the common case: with `NULL` args the columns are
padded apart and a bound that allowed a straddling match still carves correctly. It only breaks on
an **empty** args column next to content ending in a pipe. Both `TranslationLineCarverTests` suites
and the `BothCarvers_OnALineWithEmptyArgsColumns_…` parity theory exist specifically to reach it —
verified by mutation: widening either bound by one turns 335 tests red, and left every suite green
before they were added.

A line is malformed when the two passes cross — the content start would sit past the content end.
That is the same rejection the old "fewer than 6 fields" check produced, restated for a scan.

For content with no pipe run at a boundary the new carving and the old `Split` agree on every
field, so this is a strictly widening fix: nothing that parsed correctly before parses differently
now.

The rule is stated once per context, next to that context's escaper — the contexts share the file,
never code (CLAUDE.md), and `ParserContractParityTests` keeps the two copies honest.

### 3. A malformed args column rejects its row and is reported

The bare `catch { return null; }` is gone. Both parsers now validate the args columns
**syntactically**: absent (`NULL`, empty, whitespace) or a non-empty `-`-separated list of ASCII
decimal integers, nothing else. A column that fails is a malformed row:

- **Patcher** — `ParseLine` answers with `Result.Failure`, and `ParseFile` now **returns** the
  per-line warnings it already collected instead of dropping them on the floor. They flow into
  `PatchSummaryResponse.Warnings`, which `PatchCommand` already prints.
- **TMS** — the line yields an `ExportParseError`, which the import already collects and fails the
  whole upload on.

**Range and arity stay downstream**, where they can actually be checked: `Fragment.TryReorderArgRefs`
knows how many argument references the fragment has and already warns when the order does not fit.
The parser cannot know that, so it does not guess.

Rejecting the row was chosen over the softer "keep the text, drop the args with a warning". Applying
Polish text with the wrong argument order renders placeholders in the wrong positions — a silent
in-game defect — whereas a rejected row simply leaves the English text in place and says so. The
file is machine-generated; a malformed args column means the file is corrupt or hand-edited, and the
TMS import already fails closed on a bad `file_id` for the same reason.

Returning the parser's warnings is part of the fix, not scope creep. Without it, replacing a silent
null with a rejected line would only move the loss: the whole translation would disappear instead of
just its argument order, and still without telling anyone.

Two consequences of that channel, both deliberate:

- **A file whose every line is rejected fails with the reason attached.** The CLI prints the warning
  list only on a *successful* patch, so on the `NoTranslations` failure path the diagnosis has to
  ride inside the `Error` itself (`NoTranslationsEveryLineRejected`). Otherwise the one case where
  the file is most broken would be the one case that stays silent. An empty or comments-only file is
  not a corruption and still reports the plain `NoTranslations`.
- **The warning list is capped at 100, mirroring the import's `MaxCollectedParseErrors`** (spec
  0006). Every warning quotes a whole line and a real `polish.txt` is ~790k rows, so an uncapped
  list would bury the console. `TranslationParseResult.RejectedLineCount` stays uncapped, so the
  reported scale is always the true one and the list ends with an explicit "… and N more".

## Consequences

### Good

- Content ending in any number of `|` round-trips byte-exact through both parsers, in both
  directions.
- The args columns can no longer be polluted by content, in either context.
- A malformed args column reaches a human: the CLI prints it, the import rejects the upload and
  names the line.
- No format change. Every existing `exported.txt` and `polish.txt` keeps parsing — and starts
  parsing *better*, with no re-export, no migration and no CLI/TMS version skew. ADR-0039 had to
  accept two skew edges; this has none.
- The two pinning tests from #597 flip to exact round trips, and the golden fixture gains a
  trailing-pipe row so the parity suite drives the case through both parsers.

### Neutral

- `ITranslationParser.ParseFile` now returns `TranslationParseResult` (translations + warnings +
  the uncapped rejected-line count) rather than a bare list. The seam is patcher-internal — three
  call sites — and the warnings channel it feeds already existed.
- Carving costs two forward and three backward `IndexOf`/`LastIndexOf` scans per line instead of
  one `Split`, and allocates no intermediate array. Not measurable next to the per-row work on
  either side.

### The limit of this fix

A **non-conforming** writer can still produce a line no reader can resolve — put a raw `|` into an
args column and the trailing anchor lands in the wrong place. That is out of reach of any parser
that does not escape the separator, and it is not a case the pipeline produces: both writers emit
the args columns from validated data. The syntactic args check in §3 is what turns such a line into
a reported error rather than a silent mis-parse.
