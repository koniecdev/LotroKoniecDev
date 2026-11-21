# ADR-0073: A translation proposal is its own aggregate root

**Status:** Accepted
**Date:** 2026-11-13, updated 2026-11-21 (#1494)
**Decision-makers:** Solo maintainer (owner decision, 2026-11-13, after comparing both options)
**Related:** spec 0014 (translation review flow); `TranslationSystem.Domain/Aggregates/TranslationAggregate/`,
`TranslationSystem.Persistence/Bulk/BulkTranslationInserter.cs`,
`TranslationSystem.API/Features/Import/ImportExportedTexts.cs`,
`TranslationSystem.API/Features/Translations/{UpsertTranslation,ApproveTranslation,BulkApproveTranslations}.cs`;
tickets #1384, #1388, #1497, #50, #1345, #1385, #1368, #1488, #1476, #1372, #1373, #1421, #1422; ADR-0023, ADR-0024, ADR-0040, ADR-0043, ADR-0047, ADR-0065, ADR-0072

## Context

Spec 0014 turns every save into a proposal. A proposal keeps its text and author for good. Only an
approval makes it the live text that goes into `polish.txt`. The history of a row is made of its
proposals and, from #1422, the admin's manual status changes. The spec needs the proposals for undo
(#50), for a rejection with a reason (#1345), and for marking AI drafts (#1373) and pattern saves
(#1372).

The question is where a proposal lives: as a child entity inside the `Translation` aggregate, or as
its own aggregate root next to it.

Code facts that decide it:

- `Translation` is loaded through EF in large numbers. The import applies source changes, removals
  and restores (`ApplySourceChange`, `MarkRemoved`, `Restore`) to rows it loads with
  `ITranslationRepository.GetByIdsAsync`, in chunks of `ImportSettings.ApplyChunkSize` (5,000).
  `BulkApproveTranslations` loads up to 100 rows at once.
- New rows from an import go through `COPY` (`BulkTranslationInserter`), not through EF.
- `Translation` uses PostgreSQL's `xmin` as its concurrency token, so a change built on a stale row
  fails instead of overwriting.
- The bulk paths that come next (#1372 pattern saves, #1373 AI uploads) add one proposal per row and
  check exactly the rules a proposal must follow: at most one waiting proposal, and the author never
  approves a text that was never live.

## Decision

### 1. `TranslationProposal` is its own aggregate root

A proposal lives in its own table and has its own repository. It holds: text, author, created at,
origin (a person's save, a pattern save, an AI draft, the backfill, a restore, an approval again
after an English change) with a link to the pattern save, the AI upload or the proposal it copies (a
restore, an approval again) where there is one, outcome, decided by, decided at, and the rejection
reason. The first implementation ticket defines every origin value and every outcome value at once,
in the domain and in the contracts: EF and the frontend's JSON reader both fail on an enum name they
do not know, so a value added later would break the previous revision.

Names in code: the origin is not called "source", because `TranslationSource` and
`Translation.Source` already mean the English text. The restore step is not called `Restore`,
because `Translation.Restore` already undoes a soft removal.

Proposals are only appended. The text never changes, and the outcome is set once: waiting →
approved, rejected, replaced or withdrawn. A restore, and an approval of the live text again after an
English change, add a new proposal that is approved at once. Its author is the author of the copied
proposal, and it is decided by the person who approved it. So every approval is one row in the
history, and the live text is always the approved proposal with the latest decision time.

### 2. `Translation` keeps the live text and the waiting proposal's id as plain columns

`Translation` keeps the live text, as it does today, and a `WaitingProposalId` column. It enforces
its own rules on its own `xmin`:

- at most one waiting proposal (a new save replaces the waiting one, spec 0014 Q2);
- approve, reject and withdraw name a proposal they may take, otherwise the request fails with 409,
  except a repeat of the caller's own request that already took effect, which answers 204 (spec
  0014, "Retries");
- it takes no write while the previous app revision's last write is not yet reconciled (spec 0014,
  "The deploy window"). It can tell from a stamp that its own write methods keep equal to
  `UpdatedAt`.

`Translation` cannot tell an older approved proposal from the live one by itself, so the handler
loads the named proposal and the live one and passes them in; `Translation` decides. The author
rule (#1345 brings it for reject, #1368 for approve) lives here too: given the proposal's author and whether that text was ever live,
`Translation` refuses an author who approves or rejects their own text that has never been live.
The link factory mirrors the same rule (ADR-0040).
`WaitingProposalId` is cleared when the waiting proposal is approved, rejected or withdrawn.

Reason: `Translation` stays small and is always fully loaded. A child collection would mean either
an `Include` of a history that only grows, in every import chunk and every bulk approve, or a
half-loaded aggregate where an empty list silently passes the rules. Every later collection would
add to that cost, and `Translation` would turn into a god aggregate.

### 3. Save, approve, reject and withdraw change two aggregates in one transaction, on purpose

Each of these writes a proposal and updates its `Translation` in one PostgreSQL transaction, through
one `DbContext`. This breaks the usual "one aggregate per transaction" guideline, and that is a
deliberate trade-off: both rows must change together, or a row points at a proposal that does not
exist, or a proposal waits that the row does not know about.

**Do not replace this with domain events and eventual consistency.** The repo has no domain events
(CLAUDE.md, "Deliberately absent"), and a gap between the two writes would let two proposals wait at
once or let an approval miss the file rebuild. A later session that wants to change this writes a
new ADR first.

### 4. The database guards the same rules

- A foreign key from `Translation.WaitingProposalId` to the proposal.
- A partial unique index on the proposal table: at most one waiting proposal per translation.
- A `CHECK` constraint on the length of the proposal text, like the one on `Translations`
  (ADR-0043 §2), because the set-based paths skip the validator.

The `COPY` column list in `BulkTranslationInserter` (new `Translation` rows from an import) also
gets the new columns. That list is not a guard, and the columns are nullable, so a list that misses
one silently writes NULL; the first implementation ticket adds a test that the list matches the
model.

### 5. Bulk paths insert new proposal rows and never load a history

The bulk paths of #1372 and #1373 insert proposals as new rows, set-based (`COPY` or a batched
`INSERT`, their choice). They never load the history of a row to add to it. They come after #1497,
so they never write while the deploy-window stamp of spec 0014 exists. Until #1497, every write to
`Translations` goes through `Translation`, whose write methods keep the stamp, and a set-based
`UPDATE` of `Translations` waits (#1421 writes its backfill through `Translation` in chunks if it
runs in that time). In the same transaction they set `WaitingProposalId` and mark a replaced
proposal, either through `Translation` in chunks or with set-based SQL that keeps the same rules;
the partial unique index is the backstop.

The text of every proposal they add is stored after the trailing line break rule of #1476 (when the
row's English does not end with a line break, the run of CR and LF at the end goes), before the
empty and the length check. A set-based path compares each row with that row's own English.

## Consequences

### Positive

- The import and bulk approve keep loading the same small `Translation` they load today.
- The history can grow without limit and never slows the paths that do not read it.
- The rules live in one place (`Translation`), and the database also enforces the one-waiting rule.
- An approval is never lost from the history, because it is a row.

### Negative / Accepted Trade-offs

- Two aggregates change in one transaction (decision 3). This is accepted and must not be "fixed".
- `TranslatedText` keeps today's meaning for good (the live text, or the waiting text when nothing
  is live), so the previous app revision can read every row (ADR-0023). The N-1 run of ADR-0024
  runs the old tests against the new schema, but it cannot see rows the old revision writes during
  the deploy or after a rollback. Spec 0014 ("The deploy window") gives `Translations` two
  temporary stamp columns, freezes every row the old revision wrote, status-only writes included,
  until the contract step (#1388) reconciles it, and lets #1497 remove the stamp later. Only the
  first implementation ticket writes in that window, and a set-based writer waits for #1497.
- A row in "Do ponownego sprawdzenia" keeps that status through saves, rejects and withdraws while
  it has Polish, until an approval, so stale Polish never returns under changed English (ADR-0047).
  A row that loses its last Polish becomes `Untranslated` (spec 0014).
- Two sentences of ADR-0072 go stale with the first implementation ticket, which amends them: §1
  (a save with the same text takes an approved row out of the game file) and §7 (bulk approve
  never changes a text).
- ADR-0072 counts a save that replaces Polish as an overwrite. A save no longer replaces anything,
  so the first implementation ticket keeps that budget's effect: a save on a row that already has
  Polish, live or waiting, still counts as an overwrite. ADR-0072 allows deleting the overwrite
  budget once the review flow ships; that is a separate decision.
- The backfill maps a row in "Do ponownego sprawdzenia" to a waiting proposal, because today's
  data cannot tell whether its text was ever approved (an edit after an approval keeps
  `ApprovedById`).
- A restore or an approval again adds a row instead of reusing the old one. Those rows are few, and
  they are the record #1345 asks for ("every approve goes into the history").

## Alternatives Considered

### A. `TranslationProposal` as a child entity of `Translation`

One aggregate, one transaction per change, the classic DDD shape. Every load of a `Translation`
would need its proposals to check the rules: the import's 5,000-row chunks and bulk approve would
`Include` a history that only grows, or load half an aggregate and let an empty list pass the
rules. Rejected. It makes the busiest paths pay for the history they never read.

### B. Domain events between two aggregates

`Translation` and `TranslationProposal` as separate aggregates that sync through events and eventual
consistency. The repo has no domain events and no dispatcher, and a window between the two writes
breaks "at most one waiting proposal" and the rebuild of the file. Rejected.

### C. Own aggregate root, two aggregates in one transaction

Chosen. See the Decision.

## Implementation Notes

- `TranslationSystem.Domain/Aggregates/TranslationProposalAggregate/` (new aggregate, value objects
  for the text and the reason, repository interface).
- `Translation`: the live text, `WaitingProposalId`, the rules for save, approve, reject,
  withdraw, restore and approve again, and the deploy-window stamp (spec 0014).
- `TranslationSystem.Persistence`: the proposal configuration, the foreign key, the partial unique
  index, the length `CHECK`, the two stamp columns, the migration with the backfill (spec 0014
  "Backfill"), the `BulkTranslationInserter` column list.
- Read model and EF configuration in the same change (the CQRS rule).
- Implementation tickets: see spec 0014.

## References

- Spec 0014: translation review flow.
- ADR-0023 (forward-only migrations), ADR-0024 (N-1 proof), ADR-0040 (authorization-aware links),
  ADR-0043 (text length), ADR-0047 (source digest), ADR-0065 (account erasure), ADR-0072 (daily
  save budget).
- Tickets #1384, #1388, #1497, #50, #1345, #1385, #1368, #1488, #1476, #1372, #1373, #1421, #1422.
