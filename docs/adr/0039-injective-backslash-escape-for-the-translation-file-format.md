# ADR-0039: An injective backslash escape for the `||` translation file, applied by every writer

**Status:** Accepted
**Date:** 2026-08-05
**Decision-makers:** Solo maintainer
**Related:** #596 (the defect), #569 (the hostile-string coverage that found it), CLAUDE.md "Translation file format — THE inter-context contract", `ExportTextsQueryHandler`, `TranslationFileParser`, `TranslationFileSerializer`, `TranslationExportParser`, spec 0001 (update lifecycle), spec 0006 (import redesign)

## Context

The `||` file is the only contract between the two bounded contexts
(`file_id||gossip_id||content||args_order||args_id||approved`, one row per line). It has two
writers and two readers, and each context owns its own implementation by design:

| End | Component | Before this ADR |
|---|---|---|
| writer — `exported.txt` | patcher `ExportTextsQueryHandler` | escapes `CR`→`\r`, `LF`→`\n` |
| reader — `exported.txt` | TMS `TranslationExportParser` | **no unescape** — stores the escaped form |
| writer — `polish.txt` | TMS `TranslationFileSerializer` | **no escape** — emits content verbatim |
| reader — `polish.txt` | patcher `TranslationFileParser` | unescapes `\r`→CR, `\n`→LF |

One escape rule, applied at one of four ends and inverted at another. Two defects follow, and #569's
hostile-string sweep pinned both as "documented current behavior" tests:

1. **A newline in a translation silently drops the row.** Translator-submitted Polish never passes
   through any escape: the editor field is a multi-line `<textarea rows="8">`, `UpsertTranslation`
   only checks `NotEmpty()`, the aggregate stores the text verbatim, the projector selects it
   straight into `ArtifactRow.Content`, and the serializer emits it verbatim. One row becomes two
   malformed lines, both are rejected on read, the fragment never reaches the game, and nobody is
   told. The CLI auto-downloads that same artifact, so the corruption reaches the DAT patch path.

2. **The escape is not injective.** It never escapes its own escape character, so source text that
   legitimately carries a backslash in front of `r`/`n` is indistinguishable from an escape:
   `C:\notes` reads back as `C:` + LF + `otes`.

The two are one root cause: the escape was treated as a property of *one caller* (the patcher's
exporter) instead of a property of *the file format*.

## Decision

### 1. The escape is a property of the file, and it escapes its own escape character

The transform, in full:

| Value in memory / in the database | Bytes on the line |
|---|---|
| `\` (U+005C) | `\\` |
| CR (U+000D) | `\r` |
| LF (U+000A) | `\n` |

Escaping is a single left-to-right pass, so a backslash emitted by the escape is never re-escaped.
Unescaping is a single left-to-right scan: `\\`→`\`, `\r`→CR, `\n`→LF. Because the backslash is now
escaped too, the transform is **injective** and `Unescape(Escape(x)) == x` for every string.

**Unknown sequences pass through verbatim, and a trailing lone backslash is kept.** `\t` reads back
as `\t`, two characters. Rejecting them would turn a legacy file into a hard failure for no gain, and
the pair is only ever produced by a legacy writer — a conforming writer cannot emit it.

Nothing else about the format changes: the field separator, the both-ends anchoring that lets `||`
live inside content, `NULL` args, the comment marker and CRLF line endings are all untouched. In
particular **the separator is deliberately NOT escaped** — anchoring from both ends already
recovers it, both parsers already agree on it, and escaping it would break every `exported.txt` and
every `polish.txt` in existence for a problem that does not exist. (The one true delimiter defect,
content ending in an odd run of `|`, is #597 and is out of scope here. **Settled by ADR-0042**,
which kept the separator unescaped and fixed the carving instead.)

### 2. All four ends apply it

Both writers escape on write; both readers unescape on read. The asymmetry that caused the defect
is gone by construction.

### 3. The database stores raw text, never the escaped representation

`TranslationSource.Text` and `Translation.TranslatedText` hold exactly what the DAT contains and
exactly what the translator typed — real newlines, real backslashes. The escape exists only between
`Serialize` and `Parse`.

This is what makes the fix hold instead of moving the bug: with the escape at the file boundary,
there is no caller that can forget it, no column whose meaning depends on which path wrote it, and
no second representation to keep in sync. The alternative — rejecting newlines in
`UpsertTranslation.Validator` — was declined: it pushes a file-format concern into every writer,
tells the translator their perfectly legal text is invalid, and leaves the non-injectivity
(defect 2) untouched.

### 4. The rule lives in one type per context, not inline

`TranslationLineEscaper` in the patcher's `Application/Parsers/` and in the TMS'
`API/Parsing/` — a static `Escape`/`Unescape` pair each. The two contexts still share **no code**
(CLAUDE.md: "share a data contract, not code"; the architecture suite enforces it), but within a
context there is exactly one copy of the rule, callable from tests. Before this ADR the patcher's
copy was inline in a `StreamWriter` loop with no seam, which forced
`TranslationFileParserNaughtyStringTests` to keep a hand-maintained duplicate of it.

`Translation.GetUnescapedContent()` (patcher domain) is **deleted**: a third copy of the old rule,
unused by production code since the parser unescapes on parse, and a double-unescape trap for any
future caller.

### 5. Existing `TranslatedText` IS migrated; source text is not

The two columns need opposite treatment, and the difference is not a matter of taste.

**`Translation.TranslatedText` — backfilled** (`20260805122747_NormalizeTranslatedTextEscapeSequences`).
Under the old pipeline the only way a translator could put a line break into the game was to type the
two characters `\n`: the serializer emitted them verbatim and the patcher unescaped them
unconditionally. `translations/polish.txt` shows that authoring convention in the wild. Escaping on
write would turn every such row into a literal backslash-n in game — a silent meaning change to
already-published translations, with no repair path (a re-import rewrites source rows, never
translated ones). The old reader's rule is therefore applied to the column once, in SQL. This is
**behavior-preserving, not a guess**: the old pipeline mapped `\n` to a line feed unconditionally, so
no row can have meant "a literal backslash before n" — that was inexpressible. The transform is
idempotent (the replacement is a control character, so no new escape sequence can be synthesized).

**`TranslationSource.Text` / `PreviousSourceText` — not touched.** Here the stored form *is*
ambiguous, exactly because the old escape was not injective, so any script would pick one reading and
corrupt the other. The import pipeline owns the correct repair instead (below).

### 6. Line endings submitted by the editor are kept as typed

An HTML `<textarea>` submits CRLF, so a translator pressing Enter now stores `\r\n` and the DAT
receives CR+LF where the game's own texts use a bare LF. It is **not** normalized here: #596's
acceptance criteria require `\n`, `\r\n` and `\r` to survive the round trip *intact*, and silently
rewriting a translator's text is a product decision, not a transport one. The transport is lossless
either way. Whether the editor should normalize to LF on submit is left open on purpose — it needs a
look at how the DAT renders a CR before anyone changes what translators type.

## Consequences

### Good

- A translation containing newlines survives approve → artifact → download → patch with its line
  structure intact. The editor already renders source text with `white-space: pre-wrap`, so real
  newlines display correctly with no frontend change.
- Content carrying a literal backslash round-trips byte-exact in both directions.
- The two parsers now agree on escape sequences, so `ParserContractParityTests` — the drift guard —
  can assert full agreement instead of documenting a deliberate divergence.
- The two pinning tests from #569 flip to exact-round-trip assertions.

### The migration cost, and why source text is repaired by re-import instead

Existing `TranslationSource.Text` rows hold the **old escaped** representation. They cannot be
converted correctly by a script: the old escape was not injective, so a stored `C:\notes` is
genuinely ambiguous between "the DAT contains `C:\notes`" and "the DAT contains `C:`+LF+`otes`". Any
`UPDATE … replace(…)` picks one reading and corrupts the other.

The pipeline already owns the correct repair, so it is used instead: **re-export with the new CLI
and re-import**. The new export is injectively escaped, the new import unescapes it, and the stored
source becomes exactly what the DAT contains. Rows whose English text contains a newline will be
reported as source-changed and their approved Polish flagged `NeedsReview` — the honest outcome,
because the stored source for those rows really was wrong. No translation text is lost. The catalog
is admin-imported and the product is pre-launch, so this is a one-off manual step, not an outage.

Two smaller edges, both accepted:

- Re-importing an **old** `exported.txt` through the new parser reproduces defect 2 for the rows
  that trigger it (a file written before this ADR cannot be read unambiguously). Re-export first.
- The mirror image: an **old CLI** downloading a **new** artifact collapses nothing, so a `\\` pair
  reaches the DAT as two backslashes. Pre-launch with no installed base, and the CLI auto-downloads
  from the same release train — a version gate would be over-engineering today.
- The already-published `polish.txt` artifact keeps its old bytes until the next approve/import
  reschedules a rebuild. It is regenerable and self-healing; nothing reads it as authoritative.

### Neutral

- Golden fixtures on both sides gain escape rows, and the fixture is now driven through **both**
  parsers by the parity suite.
- Escape/unescape is one linear scan over each content field, allocating only when a line actually
  contains an escapable character — irrelevant next to the import's existing per-row work.
