# ADR-0003: Canonical form for `LotroNotationVersion` — collapse trailing-zero segments

**Status:** Accepted
**Date:** 2026-06-14
**Decision-makers:** Solo maintainer
**Related:** `TranslationSystem.Domain` GameVersion aggregate, ticket #132 (M2-21), spec 0001
(game-update lifecycle), ADR-0002 (TMS pivot), `docs/knowledge-base/lotro-update-history.md`
(forum version regex)

## Context

`LotroNotationVersion` is the canonical content-version identifier the whole update lifecycle keys
off (spec 0001): duplicate-registration guard (`VersionAlreadyRegistered`), forum-watcher
idempotency, and supersede/process/invalidation all assume **one canonical identity per game
version**. The VO shipped (M2-04) with a latent bug, flagged by an in-code `//todo`:

- It stored the **raw trimmed string** and validated only non-empty + `VersionMaxLength` (12).
- `Create("banana")` succeeded — despite the XML doc claiming "dotted notation", anything ≤ 12
  chars was accepted.
- Equality was raw-string (`GetAtomicValues()` yields the raw `Value`), so `48`, `48.0`, `48.0.0`
  were **three distinct versions**, and dedup — `GameVersionRepository.ExistsByVersionAsync`
  (ordinal compare) plus the EF unique index, both on the same raw column — let equivalents coexist
  as separate rows. This silently defeats every identity-keyed guard above.

Pre-release, **zero users** → changing the stored representation is free (no data migration, no
back-compat). Ticket #132 raised four open questions to settle before coding. They are **not
contested business decisions** — three are directly answerable from the empirically-settled
knowledge base / existing spec, and the fourth (the canonical form itself) is a low-stakes internal
engineering choice. This ADR records the resolution.

### Empirical grounding (the format is observed, not chosen)

The **only** producer of these strings is the lotro.com release-notes title, parsed by the regex
the patcher's `UpdateChecking` feature uses and the TMS forum watcher duplicates
(`docs/knowledge-base/lotro-update-history.md`,
`src/Patcher/LotroKoniecDev.Application/Features/UpdateChecking/GameUpdateChecker.cs`):

```
Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes
```

The captured version is therefore **always** one or more dot-separated runs of ASCII digits —
`digits(.digits)*`. Observed in the wild: `48.0`, `47.1.1`, `47.2` (2-3 segments). No prerelease
tags, no build metadata, no letters. The grammar is the documented source format, not a design
decision.

## Decision

### 1. Format grammar — `digits(.digits)*`, validated on `Create`

`LotroNotationVersion.Create` parses the (trimmed) input as **one or more non-empty
dot-separated segments, each a run of ASCII digits**, and rejects anything else with a new
`VersionProperty.InvalidFormat` domain error (`TypeOfError.Validation`). Rejected: `banana`,
`48.x`, `48..0` (empty segment), `.48` / `48.` (leading/trailing dot), `""`, whitespace-only.
A segment may carry leading zeros in the input (`047`); they are stripped on normalization (see §2).
This mirrors the regex above exactly — we enforce the format we already know the source emits.

### 2. Canonical form — collapse insignificant trailing-zero segments

The stored `Value` is the **canonical** form: parse to integer segments, drop trailing zero
segments, and re-join with `.` — keeping at least the leading (major) segment.

| Input | Canonical `Value` |
|---|---|
| `48`, `48.0`, `48.0.0` | `48` |
| `47.1`, `47.1.0`, `47.1.0.0` | `47.1` |
| `47.1.1` | `47.1.1` |
| `0`, `0.0` | `0` |
| `47.0.1` | `47.0.1` (interior zero is significant) |
| `047` | `47` (leading zeros stripped per-segment) |

Equality then **falls out of the canonical `Value`** with no `GetAtomicValues()` change: equal
canonical strings ⇒ equal VOs, equal hash codes. Both dedup paths (`ExistsByVersionAsync` ordinal
compare + the EF unique index on the same column) now correctly collapse `48` / `48.0` / `48.0.0`
to a single `GameVersion` row.

**Why collapse, not pad to a fixed segment count.** Padding (`48` → `48.0.0`) requires *inventing*
a fixed width, but observed notation already varies between 2 and 3 segments, and nothing
guarantees a future `48.0.0.1` won't appear — so any fixed width is a guess about LOTRO's
versioning we have no authority to make (it would *invent* a business constraint). Collapsing
assumes nothing about segment count, is lossless for identity (trailing zeros carry no information),
and keeps `Value` the shortest faithful representation of what the forum published. Interior zeros
(`47.0.1`) are preserved — only **trailing** zeros are insignificant.

### 3. Equivalence rule — trailing-zero segments are always insignificant

Yes, `47.1 == 47.1.0 == 47.1.0.0`. This is §2 applied generally and matches universal
dotted-numeric/semver intuition. No maximum **segment count** is imposed beyond the existing
`VersionMaxLength = 12` character cap (which already bounds practical depth); the format check
rejects garbage, the length check rejects the absurd.

> **Superseded by the 2026-08-28 amendment below (#728):** a version carries at most three segments,
> and `VersionMaxLength` is 11. `47.1 == 47.1.0` still holds; `47.1.0.0` is now refused outright.

### 4. Ordering — out of scope, no `IComparable`

`LotroNotationVersion` does **not** become `IComparable`. Supersede/processing order is `DetectedAt`
(spec 0001 — the forum is chronological), not version order. This ticket is scoped to
identity/equality only; version ordering would be a separate decision if ever needed.

## Consequences

- **Positive.** `48` / `48.0` / `48.0.0` are now one version end-to-end; the
  `VersionAlreadyRegistered` guard, forum-watcher idempotency, and spec-0001 supersede/invalidation
  all key off a single canonical identity. Garbage input is rejected at the domain boundary with a
  `Validation` error (maps to HTTP 400) instead of silently persisting. No `GetAtomicValues()` /
  EF-mapping / repository changes required — the canonical `Value` does the work.
- **Neutral.** `Value` is now a derived (canonical) string, not the verbatim forum input. The forum
  string and the canonical string are identical for the common already-minimal case (`47.1.1`); the
  only divergence is dropped trailing zeros, which carry no identity. Spec 0001 line 105 ("stored as
  the raw forum string") is superseded by this ADR for the trailing-zero case — noted there is
  unnecessary; the spec's intent (a stable per-version identity) is what this strengthens.
- **Trade-off / YAGNI.** No `IComparable`, no semver library, no configurable max-segment knob — the
  format is fixed and tiny, parsing is a `Split` + per-segment digit check. Added only what #132
  requires.
- **Data.** Pre-release, zero users, no rows to migrate; the unique index already exists on the
  canonical column, so no schema change.
## Amendment (2026-08-28) — a version has at most three segments

**Ticket:** #728 (UR-31). **Authority:** the wiki (`Wersja-gry-GameVersion`: "od jednego do trzech czlonow (...) Cztery
czlony lub wiecej to niepoprawny numer wersji"),
ruled by the owner on 2026-08-22 in #726 point 2. **Supersedes:** §3's sentence "No maximum segment
count is imposed beyond the existing `VersionMaxLength = 12` character cap".

§3 skipped a segment cap by YAGNI. That settled a business question the ADR had no authority to
settle: the wiki sentence is a **rule**, not a description of what SSG happens to publish. A tester
writing a ticket from the wiki hit the gap, and the wiki outranks this ADR (`CLAUDE.md`, "Source of
truth").

**What changes.**

- `LotroNotationVersion` accepts **one to three segments**. A fourth segment is refused with
  `DomainErrors.GameVersionEntity.VersionProperty.MoreSegmentsThanAllowed`, a `Validation` error that
  the existing `ErrorExtensions` mapping turns into HTTP 400 like every other `VersionProperty` error.
- The cap is checked on the **raw trimmed input, before canonicalization** — the same reasoning the
  length check already used. `47.1.0.0` is refused even though it would collapse to `47.1`, because
  the forum never publishes four segments, so the input is wrong whatever its canonical form is.
- The §3 equivalence example `47.1 == 47.1.0 == 47.1.0.0` loses its last term. `47.1 == 47.1.0` still
  holds, and trailing-zero collapsing is otherwise unchanged.
- **`VersionMaxLength` drops from 12 to 11.** With three segments as the ceiling, the longest input
  worth accepting is three three-digit segments plus two separators (`123.456.789`). There is still
  **no per-segment digit cap** as a rule — `123.456.789` registers, so "Update 100" is fine — but the
  character bound no longer needs the twelfth character, and a tighter bound refuses more typos.
- The column follows the constant, so this ships a narrowing `AlterColumn` migration
  (`CapGameVersionNotationToThreeSegments`) carrying the ADR-0023 §4 acknowledgment: the widest
  registered notation is 6 characters, the N-1 revision can only write values the new column still
  accepts except for 12-character typos the new code refuses anyway, and a too-long row would fail
  the ALTER at the deploy gate before any traffic moves.

**What does not change.** The canonical form, the equality semantics, the `digits(.digits)*` grammar,
the absence of `IComparable`, and the forum-watcher path (#85, post-MVP) — a hypothetical
four-segment forum title now surfaces as a visible validation error instead of silent data.
