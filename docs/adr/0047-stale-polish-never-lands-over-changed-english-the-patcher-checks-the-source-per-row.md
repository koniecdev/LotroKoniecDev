# ADR-0047: Stale Polish never lands over changed English — the patcher checks the row's source per row, at write time

**Status:** Accepted
**Date:** 2026-08-17
**Decision-makers:** Solo maintainer
**Related:** Patcher (`Features/Patching/PatchingService`, `Parsers/*`, `Infrastructure/Storage/TranslationFileCache`), TMS distribution (`Parsing/TranslationFileSerializer`, `Features/TranslationFiles/PrecomputedTranslationFileProjector`), `TranslationSystem.Domain` `SourceHash`, `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` (§"Invalidation & fallback-to-English" — amended), `docs/specs/0012-update-resilience.md` (Tier 0 + Tier 1 — amended), ADR-0045 (one Consequences bullet superseded, the verdict demoted), ADR-0039/0042/0043 (the `||` contract), tickets #659 (UR-27, the implementation), #563 (UR-20, `SourceHash`), #565 (UR-10), #566 (UR-11), #660 (UR-28)

## Context

The owner stated the rule on 2026-08-17, after an adversarial review of the Tier-1 orchestrator
ticket (#566), as an **invariant of the system**:

> If SSG changed a row's English in game version N+1 and the newest approved translation in the
> TMS is still the one for version N, the player sees English. Whatever path writes the DAT.

The reason is not cosmetic: a quest text that says the old thing lies to the player about what to
do in the game. English is a degraded session; stale Polish is a broken one.

Spec 0001 already codifies the rule as "physics" (§"Invalidation & fallback-to-English"): the
launcher chunk-patches fresh English into the player's DAT, and the TMS excludes invalidated rows
from the translation file, so `patch` never re-applies stale Polish. That mechanism is correct
**after** the new export has been imported. Between the SSG update and that import — hours to days,
the exact window in which players update and launch — nothing on the client side stops a patch from
writing the pre-update artifact over the post-update DAT. Code facts that pin the hole:

- The patcher writes unconditionally: `PatchingService.ApplyTranslations` loads the SubFile,
  `TryGetFragment`, then `fragment.Pieces = [.. pieces]` (`PatchingService.cs:157-160`). It never
  looks at what the fragment holds. Nothing in the `||` row tells it what English the translation
  was made against.
- Three paths patch inside the window. (1) The routine `launch` re-patches whenever the artifact
  hash changed (`SimplifiedGameLaunchingStrategy.cs:56-63`, `:85-99`) — one unrelated approve rebuilds
  the artifact and re-injects every pre-update row; ADR-0045 Consequences recorded this as
  "Accepted for now — revisit if a real update day shows it hurting". (2) The Tier-0 sentinel is
  gated on the server's staleness verdict, but the verdict reads "current" until an admin registers
  the new version (ADR-0045 §Context, the "window that matters most" paragraph) — the sentinel repairs
  with pre-update Polish there. (3) The Tier-1 orchestrator (#566) patches **after** the launcher's
  apply burst, on update day, by construction — with the artifact it synced at launch, which is the
  pre-update one.
- The echo-guard (#563, spec 0012) makes that window *silent* on the TMS side: an export taken
  after such a write carries our Polish for the row, the import hash-matches it as an echo of our
  own patch and treats it as Unchanged, so the row stays Approved and the artifact keeps shipping
  the stale translation. Before the echo-guard the same export at least invalidated the row
  (wrongly, with a poisoned source — the #564 class) and the artifact dropped it. The echo-guard is
  right — mass false invalidation was the larger defect — but it moves the only remaining defence
  to the write itself, which is this ADR. Until #659 lands, the manual ceremony after an SSG
  update is therefore **export and import first, patch afterwards** (a hash-changing `launch` or a
  manual `patch` between the update and the export is what writes the stale row).
- The verdict cannot close the hole, structurally: it depends on the TMS *knowing* about the new
  version (manual registration or the #85 watcher), and the client is forbidden from finding out
  itself (ADR-0045 §1: no lotro.com read; `vnum-observations.md`: the DAT carries no content version).
- The information needed to decide per row exists on both sides. The TMS computes `SourceHash` —
  a framed SHA-256 over the source triple `(Text, ArgsOrder, ArgsId)`, 128 bits kept — as the
  import diff's equality unit (`TranslationSystem.Domain/.../Services/SourceHash.cs`, #563 / spec
  0006). The patcher composes the very same triple when it exports: `text =
  string.Join(DatFileConstants.PieceSeparator, fragment.Pieces)`, args = identity `1-2-…-n` when
  `fragment.HasArguments`, else `null` (`ExportTextsQueryHandler.cs:76-90` writes the literal `NULL`
  to the file; `SourceHash.Compute` takes the persisted form, `null` — the digest is over the
  value-object form on both sides, never the wire literal). A loaded fragment
  therefore yields the digest of the English it currently holds, in milliseconds, offline.
- Once the patcher has written a row, the fragment holds Polish, not the English the digest
  describes. Re-patching an already-Polish row (a newer translation for the same English, or a
  no-op re-run) must stay possible, so "current text == expected English" alone is not enough:
  the patcher also needs to recognise **its own** earlier write.
- The `||` file is the inter-context contract (CLAUDE.md, ADR-0039/0042/0043): a column added to
  it needs golden fixtures on both sides in the same change. There are no real users yet, so the
  format change itself is free; the discipline is not.

## Decision

### 1. The invariant is enforced per row, at write time, in the patcher — nowhere else

Version bookkeeping (verdicts, registrations, watchers) tells the client that *something* may
have changed; only the row itself can tell whether *this* row did. The patcher becomes the single
point of enforcement: before it writes a fragment it checks what the fragment holds, and it writes
only when that content is one it is allowed to overwrite. Every path — `patch`, `launch`, the
sentinel, the orchestrator's branches A/B and its convergent loop — goes through the same
`PatchingService` and is covered by construction. No path is trusted to "know it is safe".

### 2. The artifact carries the source digest per row

The translation-file grammar gains a trailing seventh column, `source_digest`: the first 8 bytes
of the framed SHA-256 the TMS already computes as `SourceHash`, as 16 lowercase hex characters.

```
file_id||gossip_id||translated_text||args_order||args_id||approved||source_digest
620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||3f9a1c0e7b2d4a55
```

The projector computes it from the row's stored `TranslationSource` at generation time — an
Approved row's stored source *is* the English it was approved against, because a source change
invalidates the row (spec 0001) and approval clears the invalidation. Nothing new is persisted; the
digest that "is never persisted" for the diff becomes a wire value here, so its framing turns into a
cross-context contract and is pinned by a parity fixture (§6). The 16 hex characters are the first eight **bytes** of the digest in digest order
(`Convert.ToHexString(digest[..8])`, lower-cased) — not the `High` `ulong`'s `x16` rendering, which
is a different byte order; the parity fixture (§6) pins this. 64 bits is deliberate: the check
asks "is the current text the one specific English this row was translated from", so a collision
needs the changed English to hash to the old value — 2⁻⁶⁴ per changed row — while the full 128 bits
would add ~30% to an artifact that every approve re-ships.

**Both files carry the column.** `export` writes, per row, the digest of the very triple it just
exported (its own English + identity args) — so a translator who edits `exported.txt` by hand still
ends up with a patchable file, and the TMS import, when the column is present, recomputes
`SourceHash` over the parsed triple and rejects the row on mismatch (ADR-0042 per-row rejection):
a runtime parity check that fails loudly at import instead of as 800k `source moved` on players'
boxes. Six-column input stays readable on both ends (older exports, hand-made files, the existing
fixtures) — the last field tells them apart without ambiguity: `approved` is `0`/`1`, a digest is
16 hex characters, nothing else can occupy that slot. So both carvers keep their shape (ADR-0042:
forward two, backward *n*) with *n* sniffed from the last field (3 or 4), and the digest, being hex,
can never hold a `|`. What differs is what a *missing* digest means: on the import side it is merely
"no parity check for this row"; on the patch side it is "not patchable" (§3).

### 3. The write rule

Before `fragment.Pieces = …`, the patcher computes the digest of the fragment's export-form triple
exactly as `export` would compose it, and writes iff:

- it equals the row's `source_digest` — the fragment holds the English this translation was made
  for (pristine, or collaterally reverted by the launcher — the case Tier 0/1 exist to repair); or
- it equals the ledger's entry for `(FileId, GossipId)` (§4) — the fragment holds what this
  patcher last wrote there, so a newer translation for the same English goes through; or
- it equals the digest of **this row's own translation** in export form (the Polish text with
  identity args taken from the fragment's `ArgRefs.Count`, exactly the function §4 applies after a
  write — never from the row's own `args_order`, which may be `NULL` on a hand-made row) — the
  fragment already holds exactly what the row would write. The write is a no-op and always safe (nothing but our own patch puts
  that text there), and it (re)seeds the ledger entry. This clause is what makes a DAT patched
  before the ledger existed bootstrap by itself for every row on its current translation; a row
  holding an *older* Polish still needs the ledger (§4).

Anything else means the English moved under us: the row is **skipped and reported** as
`source moved`, alongside the existing per-row warnings (ADR-0042 style), and counted in
`PatchSummaryResponse`. Skipping writes nothing — "fallback to English requires no write of
English into the Polish field" (spec 0001) still holds; the launcher already put the English there.

A row **without** `source_digest` (a six-column translation file — hand-made, or an artifact from
before this ADR) is not patchable: **skipped by the guard with a `no source digest` warning**, never
written unguarded — and never rejected by the parser. The distinction is load-bearing: a parser
rejection of every line is `NoTranslationsEveryLineRejected`, which the launch path turns into
`RepatchFailed` and refuses to launch, and it would also break the ~30 existing parser tests that
assert six-column lines parse (patcher house rule: assertions untouched). A wholly digest-less
artifact therefore patches nothing, reports it, and lets the game start; the CLI's cached
`polish.txt` re-downloads by itself once the server regenerates with the column (ETag).

### 4. A local ledger remembers what the patcher wrote

A sidecar next to the translation file, `<file>.ledger` — the `.etag`/`.endpoint` pattern of
`TranslationFileCache` — holds `file_id||gossip_id||digest` for every row written, where `digest`
is the export-form digest of the fragment **after** the write (its Polish text with identity args —
the same function as §3, applied to what the patcher just put there). It is **upserted, never rebuilt**: a
successful write of a row upserts that row's entry; entries for rows *absent* from the current
artifact (a row edited back to Draft, a soft-removed row) or skipped in this run are **kept**. A
stale entry can never over-admit — its text sits on the fragment only if we put it there — whereas
dropping one strands a fragment holding our older Polish: edit an Approved row (Draft, out of the
artifact), let every player's next `launch` re-patch, re-approve it as P2, and a rebuilt ledger no
longer knows P1, so P2 matches nothing and the row is `source moved` on every box until SSG replaces
its SubFile. Rows whose `PutSubfileData` failed are not upserted (the DAT does not hold them). The
file is written once after the loop, atomically (temp file + rename).

A missing or unreadable ledger is treated as empty. The consequence is bounded and safe in the
invariant's direction: rows currently holding an *older* Polish are then unknown text and get
skipped with warnings until an update reverts their SubFile or the DAT is restored pristine
(the E2 restore path); rows holding the *current* translation still pass (§3, third bullet — the
no-op clause re-seeds the ledger); rows holding English still pass. Losing the ledger
can never *cause* masking, only under-patching. Nor can the mechanism *remove* a mask written before
this ADR (our Polish over new English by an unguarded patch): no clause admits a write and it never
writes English, so such a row stays stale until SSG replaces its SubFile — a legacy state, bounded
to boxes patched before #659 (today: the maintainer's). Failing toward English is the correct side.

### 5. The staleness verdict stops gating repair; the routine path needs no gate

With §1–4 in place the ADR-0045 verdict is no longer load-bearing for correctness. The Tier-0
sentinel repairs from the local artifact regardless of the verdict and regardless of connectivity —
offline repair of a collateral revert becomes safe, which the verdict design had to forbid. The
Tier-1 orchestrator's post-apply patch is safe for the same reason. The verdict and the artifact's
GameVersion metadata (#562) stay: they feed the preflight/update-check channel and the M4 UI ("your
translation file is for 49.1; the game may be newer"), they just no longer decide whether a row may
be written. Superseded in ADR-0045: the Consequences bullet that accepted routine-path masking "for now"; in
**§3** the Tier-0 rule ("force-patch only when the server says the artifact is current" and "unknown
means do not repair" *as a repair gate* — they survive only as preflight/UI semantics); and in **§4**
the sentence making #85 a dependency of #565 that "ships before the sentinel reaches players" — #85
keeps its value for the update-check channel and for the TMS learning versions early, it no longer
gates client repair. ADR-0045 §1, §2 and §5–9 stand unchanged.

### 6. Digest parity is a test, not a convention

The two contexts share the file, not code (CLAUDE.md): the patcher gets its own digest
implementation over its own triple composition. A golden fixture — a handful of triples (with and
without args, with placeholders, with the ADR-0039 escape characters in the text) and their
expected 16-hex digests — is pinned by a unit test in **both** `LotroKoniecDev.Tests.Unit` and
`TranslationSystem.Domain.Tests.Unit`, next to the `||` round-trip fixtures. The framing (marker +
UTF-16 length + UTF-16 bytes per field, `null` ≠ empty) is documented on both types. Drift fails a
build, not an update day.

## Consequences

### Positive

- The invariant holds by construction on every write path, including the two that were built to
  patch on update day (Tier 0 repair, Tier 1 orchestrator) — no path can mask, whether or not the
  TMS has learned about the new version yet.
- Correctness no longer depends on the freshness of a version registration, on the #85 watcher's
  liveness, or on being online. The "no Polish at all" window that gating everything on the verdict
  would have opened (ADR-0045 Consequences) does not open.
- The check is local and cheap: one SHA-256 over ~100 bytes per row on data already in memory —
  noise against the 14.7 s full-corpus patch (E3).
- The orchestrator's convergent loop is safe to re-run after every apply burst, and the Tier-0
  repair (E5 iteration snapshot, #565) can write the whole wiped set blindly, because admission is
  decided per row at write time — detection and admission stay separate layers.
- The bootstrap on an already-patched DAT is automatic for rows on the current translation (§4).

### Negative / Accepted Trade-offs

- A `||` format change on **both** files: both parsers (accepting six and seven columns), both
  serializers, both golden fixture sets, plus a new parity fixture — one ticket (#659), landed
  together. `translations/polish.txt`, `example_polish.txt` and the E2E artifacts get real digests
  (the `export` header keeps saying "translate this file", and now that still works).
- Every artifact grows by 18 bytes per row (`||` + 16 hex).
- A lost ledger under-patches rows that hold an older Polish until an SSG update touches their
  SubFile or the DAT is restored; a crash mid-patch leaves the rows written after the last ledger
  swap in the same state on the next run. Both fail toward English, never toward masking. Accepted.
- Six-column translation files stop patching (they still parse and still launch). There is no
  `--unguarded` switch, deliberately — the invariant has no operator override.
- **Poisoning can recur, and the guard fossilises it.** The echo-guard recognises only the row's
  *current* Polish (its documented limit): a row edited between the admin's last patch and their
  export comes back as the older Polish, is filed as a source change and re-poisons the source
  (#564's class). Once re-approved it ships `source_digest = digest(Polish)`, so a pristine or
  collaterally-reverted fragment (English) never matches — that row is lost for Tier-0 repair on
  every such box, with a false `source moved` diagnosis, until repaired the #564 way. Ceremony
  after #659: **re-patch with the current artifact immediately before every export** (safe only
  with the guard in place — before #659 the opposite rule holds, see Context). The structural fix is
  the row's translation history (TP-15 / #50) or the pristine-source direction of spec 0012.
- **Deploy ordering.** The stored artifact is regenerated only on the next approve/import signal
  (`TranslationFileRebuildWorker`), so without a one-time regeneration every upgraded CLI would sit
  on a six-column artifact — patching nothing — until someone approves something. #659 ships the
  regeneration with the deploy (Implementation Notes).
- The English text is still not present on the wire, so a translator-facing "the source changed"
  diagnosis on the client is impossible; that stays the TMS's job (spec 0001's
  `PreviousSourceText`).

## Alternatives Considered

### A. Gate every patch path on the ADR-0045 verdict

Closes the routine-path hole ADR-0045 accepted, but not the window before the new version is
registered — the verdict reads "current" there — and it depends on the watcher's liveness (its own
listed failure mode). It also widens the "no Polish at all" window: a stale verdict blocks the
collateral repair too. Rejected. It enforces the invariant only when the server already knows,
which is exactly when it is least needed.

### B. Detect the update on the client and refuse to patch until the TMS catches up

The orchestrator sees the apply burst and the sentinel sees reverts, so update *day* is
detectable; the routine launch the day after is not, and the client is forbidden from reading the
forum (ADR-0045 §1) and cannot read a content version from the DAT. Rejected. Partial coverage of an
invariant is no coverage.

### C. Ship the source English in the artifact and compare text

Same decision as taken, with the comparison in clear text. Doubles the artifact (the source
corpus is ~83 MB exported) for no gain: equality of a digest is equality of the text. Rejected in
favour of the digest.

### D. Keep the ledger inside the TMS (per-client history) instead of a local sidecar

Turns an offline, per-machine fact ("what did *this* patcher write into *this* DAT") into server
state keyed by a client identity the system does not have and does not want (no accounts on the
player side). Rejected.

### E. Skip the ledger — compare against the source digest (and the row's own translation) only

The no-op clause (§3, third bullet) covers the re-run, but an updated translation for unchanged
English could still never land: the fragment holds the older Polish, which matches neither the
English digest nor the new translation. Only a record of what *we* wrote admits that write.
Rejected.

### F. Keep the ledger inside the DAT (the Russians' NinjaMark pattern)

A marker SubFile would travel with the DAT through restores and reinstalls, which the sidecar does
not (a `-d` DAT swap or a reinstall leaves a ledger describing another file — safe direction,
under-patching only). But it is a write into the game's archive on every patch, it is wiped by the
very chunk replacements it would need to survive, and it puts our bookkeeping where SSG's launcher
can and does rewrite. Rejected; the sidecar is keyed to the translation file next to it, and the
"ledger describes another DAT" case degrades to warnings, not masking.

## Implementation Notes

- **Contract:** `TranslationLineCarver` (both contexts) — backward search for **three or four**
  trailing separators, decided by sniffing the last field (`0`/`1` ⇒ six columns, 16 lowercase hex ⇒
  seven; anything else stays a per-row rejection as today); `Translation` (patcher) and
  `ArtifactRow` (TMS) gain a nullable `SourceDigest`; the patcher `TranslationFileParser` accepts
  both widths and **does not** reject a missing digest — that is the guard's `no source digest`
  skip in `PatchingService` (§3); the TMS `TranslationExportParser` accepts both widths and, when a
  digest is present, verifies it against `SourceHash.Compute` of the parsed triple (mismatch =
  per-row rejection, ADR-0042 style — a wrong-file upload or an implementation drift fails loudly);
  `ParserContractParityTests` gets a seven-column line; golden fixtures + round-trip tests on both
  sides; the CLAUDE.md format block shows seven columns.
- **Export:** `ExportTextsQueryHandler` writes the digest of the exported triple as the seventh
  column (same function as the guard's — one `SourceDigest` type shared by export and guard, so they
  cannot drift).
- **TMS:** `PrecomputedTranslationFileProjector` computes `SourceHash` from the row's
  `TranslationSource` and passes the 16-hex prefix to `TranslationFileSerializer`. `SourceHash`
  gains the hex-prefix accessor and a doc line saying it is now a wire value.
- **Patcher:** a small `SourceDigest` twin in `LotroKoniecDev.Application` (or Domain) that composes
  the export-form triple from a `Fragment` — shared with `ExportTextsQueryHandler`, so export and
  guard cannot drift; the guard in `PatchingService.ApplyTranslations` between `TryGetFragment` and
  the write; `source moved` / `no source digest` warnings and counters in `PatchSummaryResponse`.
- **Ledger:** `ITranslationLedger` port in `Application/Abstractions`, `TranslationFileCache`-style
  implementation in `Infrastructure/Storage` (`<file>.ledger`, atomic swap); read once before the
  loop, **merged** after it — upsert written rows, keep everything else, never upsert a row whose
  `PutSubfileData` failed (§4).
- **Regeneration on deploy:** the API regenerates the stored artifact once at startup when it
  predates the column (a format stamp in the artifact header comment, or the absence of the column
  in its first data row) — otherwise upgraded CLIs patch nothing until the next approve.
- **Parity:** one fixture file linked into `tests/LotroKoniecDev.Tests.Unit` and
  `tests/LotroKoniecDev.TranslationSystem.Domain.Tests.Unit`.
- **Docs:** spec 0001 §"Invalidation & fallback-to-English" names the pre-import window and points
  here; spec 0012 Tier 0 (verdict no longer gates repair; offline repair allowed) and Tier 1 (loop
  and branch B patch only through the guard); ADR-0045 Consequences bullet marked superseded;
  CLAUDE.md house rule (the invariant, one line).
- **Poisoned sources first (#564):** a row whose stored source is our Polish (the rows #564
  repairs) gets `source_digest = digest(Polish)`, so a pristine fragment (English) never matches
  and an already-patched one always does — the guard fossilises the poisoning for pristine and
  reverted boxes. #564 is an owner-run data repair (the English lives in the gitignored exports),
  not a code dependency of #659: land it **before the first guarded artifact reaches a player**,
  and expect it to recur until TP-15 (see Consequences).
- **Warnings are aggregated, not streamed — inside `PatchingService`, not the CLI:** `source moved`
  / `no source digest` can hit thousands of rows in one run (a major that changed 1,644 sources — U49;
  a whole six-column artifact); today `PatchSummaryResponse.Warnings` is a flat list that
  `PatchCommand` prints and `SimplifiedGameLaunchingStrategy` logs one by one, so the count + bounded
  sample must be produced where both paths meet (precedent: the parser's 100-warning cap +
  "…and N more").
- **Tickets:** #659 (UR-27) implements this ADR and blocks #565 and #566; #564 (UR-21) precedes the
  first guarded artifact in production (ordering, not a code dependency); #660 (UR-28 / E6) is the
  separate branch-B quiesce question and is unaffected by this decision.

## References

- Spec 0001 — `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md`,
  §"Invalidation & fallback-to-English (the physics)"
- Spec 0012 — `docs/specs/0012-update-resilience.md`, Tier 0 / Tier 1 / Q4 (amended 2026-08-17)
- ADR-0045 — the game version reaches clients through our API (verdict; §Consequences bullet
  superseded here)
- ADR-0039 (content escape), ADR-0042 (carving, per-row rejection), ADR-0043 (DAT bounds) — the
  `||` contract this ADR extends
- `docs/knowledge-base/update-49/RESULTS.md` — E1-F1 (launcher writes on every start), E2
  (download leaves the DAT free; apply is a ~1 s burst; the forced-downgrade replay), E3 (14.7 s
  full-corpus patch)
- `docs/knowledge-base/vnum-observations.md` — no content version in the DAT
- Tickets #659, #563, #565, #566, #660; #656 (E5 per-SubFile snapshot — detection of *where* the
  launcher wrote, complementary to this ADR's *whether a row may be written*)
