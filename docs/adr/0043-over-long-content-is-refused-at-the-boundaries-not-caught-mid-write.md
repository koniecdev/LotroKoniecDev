# ADR-0043: Over-long content is refused at both boundaries, never caught mid-write

**Status:** Accepted
**Date:** 2026-08-06
**Decision-makers:** Solo maintainer
**Related:** #598 (the defect), #569 (the hostile-string coverage that found it), ADR-0023 (forward-only, N-1-compatible migrations), ADR-0042 (the sibling parser defect), CLAUDE.md "Translation file format — THE inter-context contract", `PatchingService`, `Fragment`, `UpsertTranslation`

## Context

A fragment's text pieces are written into the DAT behind a variable-length length prefix whose
two-byte form tops out at `0x7FFF` — 32767 UTF-16 code units. `VarLenEncoder.Write` guards that:

```csharp
ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxTwoByteValue);
```

Throwing is right. A truncated prefix would desynchronise the reader for **every following fragment
in the subfile**, so a writer that cannot express the length has no correct output to produce.

Nothing upstream stopped an over-long value from getting there. `UpsertTranslation.Validator` checked
only `NotEmpty()`, `Translation.ProvideTranslation` stored verbatim, and the column was unbounded
`text` — so a 40000-character translation was accepted, approved, published into the artifact, and
became a crash on someone else's machine hours later. `PatchingService.ApplyTranslations` is
`try { … } finally { Flush; Close }` with no `catch`, and `ApplyPatchCommandHandler` has none either.

Two claims in the defect report needed checking against the code, and one of them was wrong:

- **`patch` does not leave a partially patched DAT.** `PatchCommand` takes a backup before patching
  and its `catch (Exception)` restores it, returning exit code 3. The DAT is repaired; what the user
  gets is a stack trace instead of an error message.
- **`launch` does.** `LaunchCommand` has the same `catch` but **no backup and no restore**, and
  `SimplifiedGameLaunchingStrategy` calls the same `ApplyTranslations`. Because `PutSubfileData` is
  called per subfile as the loop advances, every subfile processed before the throw is already
  committed, and `finally` flushes. That is the path that actually corrupts, and it is the default
  one users run.

The exposure is real but narrow: the artifact is machine-generated from validated rows, so an
over-long piece can only arrive through a hand-edited or hostile `polish.txt` — or through the TMS,
once the missing cap let one in.

## Decision

### 1. The TMS refuses over-long text at the API, and again at the column

`UpsertTranslation.Validator` gains `MaximumLength(DatFormatConstants.MaxTranslatedTextLength)`, so
the translator gets a 400 while editing instead of the patcher getting an exception. The constant
lives in `TranslationSystem.Primitives/Constants/`, mirroring where the patcher keeps
`DatFileConstants` — the two contexts share a data contract, not code, so each states the format
fact in its own layer.

The TMS caps the **whole text**; the DAT caps a **piece**. That is deliberate. The patcher cuts
pieces on `<--DO_NOT_TOUCH!-->`, and teaching the TMS that rule would duplicate patcher logic across
the context boundary for no gain. A whole-text cap is the strictest bound expressible without it and
can never be wrong in the unsafe direction — a text within 32767 cannot produce a piece above it.
It is theoretically over-strict for a multi-piece text summing past the limit. Measured on the
shipped corpus (`data/exported.txt`, 792,500 rows): the longest English source is **5,959**
characters, the average is **66**, and **zero** rows are above 32767 — so nothing real is within
5.5× of the cap and the extra strictness costs nothing.

`Translation.ProvideTranslation` gains a matching guard. Like the `ThrowIfNullOrWhiteSpace` beside
it, this is a **programmer-error assertion, not a per-row validation failure**: the boundary that
turns it into a message is the validator, and the domain merely refuses to hold a value it knows is
unusable.

The editor's textareas carry a matching `maxlength` so a translator is stopped while typing rather
than on submit. It is a nicety, not enforcement — and it is deliberately *not* exact: HTML form
submission normalizes textarea newlines to CRLF, so a draft of exactly 32767 characters containing
line breaks arrives one byte longer per break and still earns its 400. That only bites within a
handful of characters of a cap nothing real comes within 5.5× of, so tightening the client bound to
compensate would trade a real invariant for a hypothetical one.

### 2. The database backstop is a CHECK constraint, not `varchar(n)`

`HasMaxLength(32767)` was the obvious move and is the wrong one. Narrowing `text` → `varchar(32767)`
rewrites the whole table and rebuilds its trigram GIN index while holding `ACCESS EXCLUSIVE`.
`Translations` carries the ~780k-row corpus (spec 0001), and under ADR-0023 the previous revision
keeps serving throughout every migration — so that lock is exactly the deploy-window outage the ADR
exists to prevent.

The same guarantee ships as `ADD CONSTRAINT … NOT VALID` followed by a separate `VALIDATE
CONSTRAINT`: the first takes `ACCESS EXCLUSIVE` only for the catalog write — it reads no rows — and
the second takes `SHARE UPDATE EXCLUSIVE`, which lets reads and writes through for the scan. No
rewrite, no index rebuild.

**The two statements must live in two migrations, and that is the whole point.** PostgreSQL holds
every lock until its transaction commits, and EF applies each migration in one transaction, so
putting both statements in one file would hold the `ACCESS EXCLUSIVE` from the `ADD` across the
`VALIDATE` scan — reproducing exactly the lock profile this form exists to avoid. Two migrations are
two transactions: the lock is released after the declare and reacquired at the weaker level for the
validate. It also makes the failure mode safe — the NOT VALID constraint is already committed, so it
binds every new write even if the scan then fails on legacy data, and a re-run retries only the
validate. (In one migration, a failed `VALIDATE` would roll the `ADD` back with it and leave nothing
behind.) The scan itself is sub-second at this table size; the split is cheap insurance and, more
importantly, the only version of this design whose stated properties are actually true.

The bound is PostgreSQL `length()` — code points — while the DAT counts UTF-16 code units, so a text
made entirely of astral-plane characters could pass the constraint and still be refused above it.
Accepted: the exact measure belongs in C#, where `string.Length` already *is* that unit, and a
backstop that over-rejected legitimate Polish would be the worse error.

### 3. The patcher screens each row before it can touch a subfile

`Fragment.IsWritablePiece` states `Write`'s precondition as a predicate. `PatchingService` evaluates
it per row alongside the existing `not found in DAT` / `not a text file` checks — **before** a
subfile is loaded, and well before one is mutated — and turns a failure into a warning plus a skip,
the same shape every other unapplicable row already gets.

Placement is the whole point. Screening before mutation means a bad row costs exactly one row: the
run continues, the remaining rows apply, and nothing is committed on its account.

### 4. `PatchingService` does not catch, and does not stage the file

The defect report asked whether the service should catch, or stage every modified subfile and commit
at the end. Both are rejected.

**No catch.** With §3 in place, `Serialize()` is total over anything the parser can produce: pieces
come either from the DAT — writable by construction, since they were read from a length prefix that
could express them — or from a screened translation. Argument strings and group counts round-trip
from the DAT untouched and are bounded the same way. A blanket `catch` would therefore catch nothing
real, while converting genuine programmer errors into user-facing failures and hiding them from the
tests. Errors are values here, not exceptions (CLAUDE.md); the invariant is enforced at the one
mutation point rather than mopped up afterwards.

**No staging.** Buffering every modified subfile to commit at the end would hold a large fraction of
the game's text corpus in memory to protect against a failure mode §3 removes, and the native DAT
API offers no transaction to commit into anyway — `PutSubfileData` is the commit. It also would not
achieve atomicity, only move the partial-write window from "some subfiles" to "the flush".

What remains worth fixing is separate and out of scope here: **`launch` patches without a backup**,
so any future unhandled failure on that path still leaves a half-patched DAT where `patch` would
self-repair. A partially patched DAT is recoverable — it is a valid DAT, and a re-patch applies the
rest (`docs/knowledge-base/`) — but the asymmetry between the two commands is not deliberate.

## Consequences

### Good

- An over-long translation is refused where a human is looking at it, with a validation message,
  instead of becoming an exception on a player's machine after the artifact ships.
- One unwritable row in a hand-edited `polish.txt` costs one warning, not the patch run — and the
  `launch` path can no longer be aborted mid-write by content.
- Three independent layers (validator, domain guard, CHECK constraint), each meaningful on its own,
  and none of them a `catch`.
- The database gains the constraint without a table rewrite, so the migration is N-1 safe under
  ADR-0023 rather than an outage.

### Neutral

- `Fragment.Write` still throws for an over-long piece. It is now a deliberate last resort behind a
  public predicate rather than a reachable failure mode, and `FragmentNaughtyStringTests` pins both
  halves — that the predicate refuses the piece, and that `Write` never degrades to truncation.
- `PatchingService` computes `GetPieces()` once per row and reuses it at the assignment, so the
  screen costs no extra split.

### The limit of this fix

The cap is on what the TMS and the patcher will *accept*, not on what the DAT can hold — a fragment
whose original English already exceeds the ceiling would be unpatchable in Polish too. No such
fragment exists in the corpus, and one appearing would be a game-side change worth a knowledge-base
entry rather than a code path.
