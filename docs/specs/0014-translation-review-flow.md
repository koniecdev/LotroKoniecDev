# Spec 0014: Translation review flow: proposals, rejection with a reason, history

- **Status:** Agreed (2026-11-13; every owner question answered)
- **Date:** 2026-11-12, updated 2026-11-13 (owner decisions in #1345, #1368, #1369, #1372, #1373,
  the domain model, and the owner's answers to Q1–Q9) and 2026-11-21 (#1494: the decisions made
  after 13 November, see "Changes of 2026-11-21")
- **Author:** koniecdev
- **Ticket:** covers #49 (TP-14 review workflow), #50 (TP-15 history), #1345 (rejection with a
  reason) and the review points of #1332. Implementation tickets: #1476 (trailing line breaks)
  lands first, then #1384 (every save is a proposal). Once #1384 runs on prod: #1388 (its contract
  step) and #1386 (the wiki's "coming" mark). After #1388: #50 (history and restore), #1345
  (rejection with a reason) and #1385 (withdraw), and later #1497 (it removes the deploy-window
  stamp). #1368 (the Reviewer's right to approve, reject and restore, and the author rule) after
  #50 and #1345. The Reviewer role itself, the admin page that gives it, and its limits come
  earlier, in #1488, which needs nothing from this spec (owner decision of 2026-11-21: #1368 is
  split).
- **Related:** spec 0001 (status model, approve = publish), spec 0007 (bulk approve), ADR-0040
  (authorization-aware links), ADR-0043 (text length), ADR-0047 (source digest), ADR-0065 (account
  erasure), ADR-0023 (forward-only migrations), ADR-0072 (daily save budget), #687 (SEC-09, one
  hijacked translator account), #1488 (the Reviewer role and its limits), #1368 (the Reviewer's
  right to approve; nobody approves their own text), #1476 (trailing line breaks), #1421 and #1422
  ("Nie do tłumaczenia"), #1369 (glossary), #1372 (pattern saves), #1373 (AI draft uploads), wiki
  `Wersje-systemu`, `Mechanizm-diff` and `Import-do-TMS`. ADR-0073 records the domain model.

## Business context

The first outside translator started working on 2026-11-11. Two gaps showed up on the first day:

- Nobody can send a text back. A translator wrote "Wysoki elf" for "High Elf"; the agreed term is
  "Elf wysokiego rodu". Today the admin can only approve it or overwrite it, and the translator
  never learns why.
- A save overwrites the text in place. Nothing keeps the previous text, so a mistake or a vandal
  cannot be undone (#50), and the translator cannot see what was there before (#1332).

A third problem is less visible: when a translator edits an approved text, the row becomes a draft
and leaves `polish.txt` until the admin approves it again. Players would see English in the
meantime, and the approved counter drops. It also means any translator account can pull approved
texts out of the game just by editing them (#687).

On 2026-11-13 the owner decided more of the flow:

- Approval is no longer admin-only. A new Reviewer role ("Recenzent"), granted by the admin,
  approves texts, and nobody approves their own text, the admin included (#1368). On 2026-11-21
  the owner split this in two: #1488 brings the role and its limits first, and #1368 later gives
  the role the right to approve, reject and restore.
- The same people send a text back, always with a reason, and never their own text (#1345).
- Proposals will also come in bulk: one pattern save fills a whole group of rows (#1372), and the
  admin uploads AI drafts (#1373). Both must show in the history where a text came from.

## Goal

A translator can propose a text and later see whether it was approved or rejected, and why. The
text players get changes only when a reviewer or the admin approves a new one, and a text that
was never live is never approved by its own author. Every version of every text is kept, with
its author, its origin and its outcome, and a reviewer or the admin can put an older version
back.

## In scope

- **Every save is a proposal.** A save no longer overwrites the text. It adds a proposal (text,
  author, time, origin). Proposals are never edited or deleted.
- **The game keeps the approved text.** Each row has at most one live text: the last approved
  proposal. `polish.txt` is built from live texts. A new proposal does not change the file.
- **Approve a proposal.** A reviewer or the admin who is not the proposal's author approves one
  specific proposal (the one on their screen). It becomes the live text and the file is rebuilt.
  For a text that was already live, see the exception under Business rules.
- **Reject a proposal with a reason.** A reviewer or the admin who is not the proposal's author
  rejects one specific proposal and writes why. The reason is required. The translator sees the
  reason in the editor and can find their rejected proposals on the list. There is no e-mail (Q3).
- **Withdraw.** The author of a waiting proposal withdraws it, for example after saving by
  mistake (Q7). It goes to the history as "withdrawn", and the row has no waiting proposal any more.
- **History.** The editor shows the history of a row. It has more than one kind of entry. Every
  proposal is one: text, author, time, origin (a person's save, a pattern save, "szkic AI z wgrania
  nr N", the backfill, a restore, an approval again) and outcome (waiting, approved, rejected with
  the reason, replaced, withdrawn). From #1422, every manual status change by the admin ("Nie do
  tłumaczenia" set or taken off) is one too, with who did it and when (wiki `Mechanizm-diff`).
  Every translator sees the history of every row (Q5).
- **Restore.** A reviewer or the admin puts an older approved proposal back (Q9 (a)), also one they
  wrote themselves, because that text was already in the game (Q9 (b)). It becomes the live text
  and the file is rebuilt. This is the undo path #50 asks for. A restore does not touch a waiting
  proposal: it stays waiting.
- **Bulk approve (spec 0007)** approves, for each selected row, the proposal named by its id: the
  waiting proposal, or the live one of a row in "Do ponownego sprawdzenia" (approve again, as bulk
  approve does today). It skips a waiting proposal the caller wrote and counts it in its summary
  (#1368).
- **Proposals in bulk.** A pattern save (#1372) and an AI upload (#1373) add one proposal per row,
  each marked with its origin and the save or upload it came from.
- **List and counters.** New labels and filters for "waiting for review", "change waiting" (live
  text plus a newer proposal) and "rejected". A row with no live text whose last proposal was
  rejected shows "Odrzucone" until the next save (Q8). Editing an approved text no longer lowers
  the approved counter.
- **Backfill.** Every existing row with Polish (`TranslatedText` is not NULL, removed rows
  included) gets its current text as its first proposal, with the origin "backfill", so the history
  starts with what is there today. A removed row it skipped would stay frozen ("The deploy
  window"), and an import that brings it back would bring it back frozen. An `Approved` row's text
  becomes an approved proposal and stays live. A `Draft` row's text becomes the waiting proposal.
  A "Do ponownego sprawdzenia" row's text becomes the waiting proposal too: today's data cannot
  tell whether that text was ever approved, because an edit after an approval keeps the approver.
  A backfill proposal's author is the row's `SubmittedById`; an approved one is decided by the
  row's `ApprovedById`. Its created at, and for an approved one its decided at, are the row's
  `UpdatedAt`. That time can be later than the save (an import, an approval or a removal also
  moves it), so the history marks backfill times as approximate. The backfill keeps every row's
  status. One cost of the safe mapping: a "Do ponownego sprawdzenia" text that was live before the
  migration is not covered by the Q9 (b) exception, so its author cannot approve it again alone.
  On 2026-11-13 prod had no such rows.

## Out of scope

- Glossary (#1369).
- E-mail or other push notifications. The translator learns about a rejection in the app only
  (Q3).
- Discussion threads. A rejection carries one reason; there are no replies.
- Bulk reject.
- A "Sprawdzone w grze" mark, and a preview file with waiting proposals for testing in the game
  (Q4 chose option A).
- The pattern save and the AI upload themselves (#1372, #1373). This spec gives them the proposal
  they write.
- AI review (#51), per-language roles (#48).
- Any change to the `||` file format or the patcher.

## Business rules & edge cases

- **A save never changes what players get (Q1).** Only an approve does: of a new proposal, of an
  older one (restore), or of the live text again after the English changed. This is criterion 2 of
  1.0 in the wiki (`Wersje-systemu`: "Zapis tworzy propozycję"). It reverses the old wiki rule in
  `Mechanizm-diff` ("Kiedy tłumacz zapisze poprawkę, wiersz wraca do statusu Wersja robocza"). The
  wiki already says the new rule, marked as coming with #1384 (wiki-first rule).
- **Approve and reject name the proposal.** The request carries the proposal id the reviewer saw.
  Reject takes only the waiting proposal. Approve takes the waiting proposal, an approved proposal
  other than the live one (restore), or the live one of a row in "Do ponownego sprawdzenia"
  (approve again). Any other id (a proposal a newer save replaced, one someone else already decided,
  a withdrawn or rejected one, the live one of a row that is not in "Do ponownego sprawdzenia")
  fails with a conflict, and nothing changes. The one exception is a repeat of the caller's own
  request that already took effect, which answers 204 (see "Retries"). So nobody approves a text
  they have not seen. Today this can happen: approve takes only the row id.
- **Every approval is a row in the history.** A restore, and an approval of the live text again
  after an English change, add a new proposal with that text, approved at once, whose origin names
  what happened ("restore of proposal N", "approved again after an English change") and links to
  the proposal it copies. Its author is the author of the copied proposal; it is decided by the
  person who approved it, at that moment. So the history stays append-only, an outcome is still
  set once, and the live text is always the approved proposal with the latest decision time.
- **Nobody approves or rejects their own text that has never been live,** the admin included
  (#1368, #1345). "Author" is the proposal's author, not `Translation.SubmittedById`. A single
  approve or reject refuses it with its own error. Bulk approve skips it and counts it.
- **Exception (Q9 (b)): a text that was already live.** Its author may restore it, and may approve
  their own live text again after an English change. Such a text brings in nothing new.
- **Withdraw names the proposal too.** Only its author may withdraw it. If it is no longer the one
  waiting, the request fails with a conflict and nothing changes, except a repeat (see "Retries").
  A withdrawn proposal is never approved later; restore takes only proposals that were approved
  before.
- **Retries.** The website sends a JSON write up to two more times after a 5xx answer or a timeout
  (`HttpClientsDependencyInjectionExtensions.MayRetry`, `MaxRetryAttempts = 2`), and a person may
  also click again after a lost answer. So a repeat of the caller's own request that already took
  effect answers 204 and changes nothing:
  - an approve of a waiting proposal, when the named proposal is already the live one, the caller
    decided it, and the row is not in "Do ponownego sprawdzenia";
  - a restore or an approval again, when the live proposal is the caller's copy of the named one
    (its copy link names it) and the row is not in "Do ponownego sprawdzenia";
  - a reject, when the caller already rejected the named proposal (the first reason stays);
  - a withdraw, when the named proposal is already withdrawn;
  - bulk approve skips such an item and counts it, as it skips any other.

  A retried save adds a second proposal with the same text, which replaces the first one. This
  cost is accepted, as ADR-0072 accepts the retried save, and every save is a proposal (wiki
  `Mechanizm-diff`). Everything else stays a 409. These rules cover an answer lost after the
  request committed. A retry that overlaps an attempt still running can still get a 409 from the
  row's `xmin`, because a timed-out attempt may still be served; the editor shows the conflict, and
  the reload shows the real state.
- **The English changes (import, spec 0001).** Unchanged rule: the row becomes "Do ponownego
  sprawdzenia" and its live text leaves `polish.txt` (ADR-0047). A waiting proposal stays waiting.
  A reviewer or the admin can approve the waiting proposal, or approve the live text again ("the
  old Polish still fits"). Any approval, a restore included, clears the invalidation, as approve
  does today, and leaves a waiting proposal waiting.
- **A removed row (soft removal, spec 0001)** takes no new proposals, as today. Its proposals and
  live text are kept, so a restored row comes back with them.
- **Import writes no proposals.** An import changes only the English and the status (wiki
  `Mechanizm-diff`), so the history does not grow with imports. (#50 feared 780k history rows per
  import; that cannot happen in this model.) From #1421 the import also gives "Nie do tłumaczenia"
  to a row with no Polish (see the status rules below).
- **Account erasure (ADR-0065).** Proposals and rejection reasons stay, credited to the profile that
  becomes "Usunięte konto". The data export of a translator includes their proposals and their own
  decisions: the proposals they approved or rejected, with the reasons they wrote (today's export
  already lists the rows they approved).
- **The daily save budget (ADR-0072) stays.** A save on a row that already has Polish, live or
  waiting, still counts as an overwrite. ADR-0072 allows deleting the overwrite budget once the
  review flow ships; that is a separate decision. Two sentences of ADR-0072 go stale with #1384,
  and #1384 amends them: §1 says a save with the same text takes an approved row out of the game
  file, and §7 says bulk approve never changes a text.
- **This spec adds no status value.** A new `TranslationStatus` value would break the previous app
  revision, which reads the status as a string (ADR-0023). The fifth status, "Nie do tłumaczenia"
  (`NothingToTranslate`), comes with #1421, which plans that step itself.
  - In this spec, a row **has Polish** when it has a live text or a waiting proposal.
  - A row in "Nie do tłumaczenia" has no live text and no waiting proposal. It takes no save (wiki
    `Mechanizm-diff`), and it is outside the proposal status rules below. The admin's manual status
    change (#1422) is refused on a row with Polish, and it goes into the history (see "History").
    Whichever of #1384 and #1421 lands second adapts its code to the other.
  - **`NeedsReview` wins while the row has Polish.** A row in "Do ponownego sprawdzenia" keeps that
    status through saves, rejects and withdraws, as long as it has Polish, until an approval clears
    it. So stale Polish never goes back into `polish.txt` under changed English (ADR-0047). This
    replaces spec 0001's rule that a save on `NeedsReview` gives `Draft`.
  - **A row that loses its last Polish** (its waiting proposal was rejected or withdrawn, and
    nothing is live) becomes `Untranslated`, because "Do ponownego sprawdzenia" means that Polish
    exists (`Mechanizm-diff`). Its `PreviousSourceText` is cleared, because there is nothing left to
    compare (`Mechanizm-diff`: the column is filled only when there is something to compare). It
    shows "Odrzucone" while its latest proposal is rejected (Q8). The cost: a translator who writes
    that text again no longer sees the old English panel.
  - Any other row takes its status from the proposals: a live text is `Approved`, shown with
    "change waiting" while a proposal waits; no live text and a waiting proposal is `Draft`
    ("waiting for review"); no live text and nothing waiting is `Untranslated`, shown as
    "Odrzucone" while its latest proposal is rejected.
  - No code may assume that `PreviousSourceText` is set on a `NeedsReview` row: the deploy window
    below can leave a `NeedsReview` row without it.
- **Dates** in the history and on rejections are shown in Polish time, like every other date.
- **Who may do what.**
  - A translator proposes, withdraws their own waiting proposal, and reads the history of every
    row.
  - A reviewer does what a translator does, approves or rejects other people's proposals, and
    restores older versions.
  - The admin does what a reviewer does, and grants or removes the Reviewer role (#1488).
  - **Until #1368, only the admin approves, rejects and restores,** although the Reviewer role
    exists from #1488. #1488 gives the role only higher limits: 300 requests a minute, counted per
    account, and no daily save limit (ADR-0072). #1368 changes the approve, reject and restore
    policy from Admin to Reviewer or Admin.
  - Nobody approves or rejects their own text that has never been live (the exception for a text
    that was live is above).
  - Owner principle (2026-11-13), the reason reviewers restore too: the admin is never the only
    person who can do a routine step. In the owner's words: "Generalnie nie powinienem jako admin
    bottleneckować czegokolwiek co relatywnie mało istotne."
  - Links follow ADR-0040: `approve` and `reject` appear on a waiting proposal, and `approve` also
    on an approved proposal other than the live one (restore) and on the live text of a row in
    "Do ponownego sprawdzenia", only for a caller the policy allows. On a waiting proposal they
    never appear for its author. `withdraw` appears only on a waiting proposal, and only for its
    author. The role rule lives in the policy; the author rules are state rules (the proposal's
    author is or is not the caller), so they live in the link factory (#1368).
- **Length.** A proposal keeps the current limit (32767, ADR-0043), with a `CHECK` constraint on the
  proposal table like the one on `Translations` (ADR-0043 §2), because the set-based paths skip the
  validator. A rejection reason has its own limit: 1000 characters.
- **Trailing line breaks (#1476, owner decision of 2026-11-18).** On a save, a pattern save and an
  AI upload, the domain removes the run of CR and LF characters at the end of the text when the
  row's English does not end with a line break. It does this before its own empty and length
  guards, and the proposal stores the trimmed text. `UpsertTranslation.Validator` stays as #1476
  leaves it: it counts the text as sent. A set-based path compares each row with that row's own
  English. #1476 lands before #1384, so its one-off cleaning of the saved rows runs before the
  backfill turns them into proposals. After that, a proposal's text never changes (ADR-0073 §1).

## Contract

- **Trigger:** TMS API and the editor (`/editor/{id}`), translations list (`/translations`).
- **Save:** `PUT /api/v1/translations` (`UpsertTranslation.Command`, rel `upsert`, unchanged). It
  adds a proposal. It no longer changes an `Approved` row to `Draft` and no longer schedules a
  rebuild. Response: `TranslationDetailResponse` with the live text and the waiting proposal.
- **Approve:** `POST /api/v1/translations/{id}/approve`, body `{ proposalId }`
  (`ApproveTranslation.Command(TranslationId, TranslationProposalId)`, rel `approve`, Reviewer or
  Admin; Admin only until #1368). The id names the waiting proposal, an approved proposal other
  than the live one (restore), or the live one of a row in "Do ponownego sprawdzenia" (approve
  again). 204, also for a repeat that already took effect ("Retries"); 403 for a plain translator;
  403 with the error code `TranslationProposal.OwnProposal` when the caller wrote the waiting
  proposal (from #1368, which brings the author rule for approve); 409 for any other id, a
  proposal of another row included; 404 for an unknown translation and 422 for a removed row, as
  today.
- **Reject (new):** `POST /api/v1/translations/{id}/reject`, body `{ proposalId, reason }`
  (`RejectTranslationProposal.Command`, rel `reject`, Reviewer or Admin; Admin only until #1368;
  never the proposal's author). 204, also for a repeat ("Retries"); 400 for an empty reason or one
  over 1000 characters; 403 for a plain translator; 403 with `TranslationProposal.OwnProposal`
  when the caller wrote the proposal; 409 when the proposal is not the one waiting; 422 for a
  removed row. #1345 adds that error code, and #1368, which depends on #1345, reuses it.
- **Withdraw (new):** `POST /api/v1/translations/{id}/withdraw`, body `{ proposalId }`
  (`WithdrawTranslationProposal.Command`, rel `withdraw`, the proposal's author only). 204, also
  for a repeat ("Retries"); 403 when the caller is not the author; 409 when the proposal is not the
  one waiting; 422 as for approve (removed row).
- **History (new):** `GET /api/v1/translations/{id}/history` (`ListTranslationHistory.Query`, rel
  `history`, every signed-in translator), newest first. It holds more than one kind of entry: each
  proposal and, from #1422, each manual status change by the admin. Each entry carries `kind` as a
  plain string in the contract (`proposal`; #1422 adds its own), never an enum: the frontend reads
  enums with `JsonStringEnumConverter`, which fails the whole answer on a name it does not know, so
  a new kind would break the previous frontend. A client shows a kind it does not know as a plain
  line with its time and author. The history only grows, so #50 pages the answer, newest first,
  and picks the page size: a technical choice, not a question for the owner. Every entry stays
  reachable. An unknown translation is a 404.
  Restore is the `approve` rel on an approved proposal other than the live one, for a caller the
  policy allows.
  - **Request budget.** The editor loads the history on a GET only, like the panel of rows with the
    same text; a POST that fails validation does not load it again. It costs one request on every
    editor view, the reload after a save included. Putting the history into the detail answer would
    cost no request, but every detail and save answer would then carry a list that only grows, and
    a failed history read would fail the editor.
- **Bulk approve:** `POST /api/v1/translations/approve`, body `{ items: [{ id, proposalId }] }`,
  where each id names the waiting proposal or the live one of a row in "Do ponownego sprawdzenia".
  Summary `{ requested, approved, skipped, skippedOwn }`: `skipped` stays the total, so
  `approved + skipped = requested` still holds (INV-8.12), and `skippedOwn` is the part of it the
  caller wrote (#1368). Any other id (a stale one, or an approved proposal that is not the live
  one), a repeat ("Retries") and a row out of step ("The deploy window") are skipped. Items are
  distinct by row id, as the ids are today: only the first item for a row counts, and `requested`
  counts rows, so the sum still holds.
- **Domain (owner decision, 2026-11-13): `TranslationProposal` is its own aggregate root, not a
  child of `Translation`.** ADR-0073 records it.
  - `TranslationProposal`: text, author, created at, origin (a person's save, a pattern save, an
    AI draft, the backfill, a restore, an approval again after an English change) with a link to
    the pattern save (#1372), the AI upload (#1373) or the proposal it copies (a restore, an
    approval again) where there is one, outcome, decided by, decided at, rejection reason.
    Proposals are only appended: the text never changes, and the outcome is set once (waiting →
    approved, rejected, replaced or withdrawn).
  - #1384 defines every origin value and every outcome value at once, in the domain enum and in
    the `TranslationSystem.Contracts` enum. EF reads an enum stored as a string and throws on a name
    it does not know, and so does the frontend's JSON reader, so a value added later would break the
    previous revision.
  - `Translation` keeps the live text (as today) and `WaitingProposalId` as plain columns. It
    enforces "at most one waiting proposal" and "approve, reject and withdraw name a proposal they
    may take, otherwise 409, except a repeat (Retries)" itself, on its own `xmin`. The handler loads
    the named proposal and the live one and passes them in; `Translation` decides.
    `WaitingProposalId` is cleared when the waiting proposal is approved, rejected or withdrawn.
  - Names in code: the proposal's origin is not called "source" (`TranslationSource` is the
    English), and the restore step is not called `Restore` (`Translation.Restore` undoes a soft
    removal).
  - Why: `Translation` stays small and is always fully loaded. The import (`ApplySourceChange`,
    `MarkRemoved`, `Restore` through `GetByIdsAsync` in chunks of `ApplyChunkSize`) and
    `BulkApproveTranslations` load `Translation` through EF. A child collection would mean either
    an `Include` of a history that only grows, or a half-loaded aggregate where an empty list
    silently passes the rules. The bulk paths of #1372 and #1373 add proposals and check exactly
    those rules, and every later collection would add to that cost. The bulk paths insert new
    proposal rows set-based and never load a row's history.
  - The trade-off, on purpose: save, approve, reject and withdraw change two aggregates in one
    PostgreSQL transaction (one `DbContext`). The ADR records this as deliberate, so that no later
    session "fixes" it with events and eventual consistency.
  - Database guards: a foreign key from `WaitingProposalId` to the proposal, a partial unique index
    "at most one waiting proposal per translation", and the length `CHECK` on the proposal text.
  - The `COPY` column list in `BulkTranslationInserter` gets the new columns (`WaitingProposalId`
    and the two stamp columns, with the entity's values, NULL on a new row). The columns are
    nullable, so a list that misses one silently writes NULL; #1384 adds a test that the list
    matches the model, with no exception for the stamps.
- **Files touched:** TMS database (new table; `WaitingProposalId` and two stamp columns on
  `Translations`; expand migration plus backfill, ADR-0023). The `polish.txt` artifact keeps its
  format, and the projector does not change: it still builds the file from `TranslatedText` of
  `Approved` rows that are not removed (`PrecomputedTranslationFileProjector`). On every
  `Approved` row that is in step (see "The deploy window"), that text is the live text, and both
  app revisions build the same file from a frozen row.
- **`TranslatedText` keeps today's meaning, for good:** the live text, or the waiting text on a row
  with nothing live; NULL when there is neither (after a reject or a withdraw on such a row). So
  the previous app revision can read every row, and no later step changes the column's meaning.
  The neighbouring columns keep today's meaning too (#1347):
  - "Empty" means NULL. The panel of rows with the same text, the `conflictsOnly` filter and the
    daily save budget keep testing `TranslatedText` for NULL.
  - `SubmittedById` is the author of the proposal whose text is in `TranslatedText`, and NULL when
    `TranslatedText` is NULL. A save on a row with a live text does not change it.
  - `ApprovedById` is the approver of the live text. On a row with no live text it may keep an
    older value, as a `Draft` row does today, so the data export takes a translator's approvals
    from the proposals, not from this column (#1384).
  - A waiting change on an approved row does not show in the panel of rows with the same text: the
    panel reads `TranslatedText`, which holds the live text.
- **The deploy window.** During the deploy of #1384, and after a rollback, the previous revision
  still writes the old way: its save writes `TranslatedText` and sets `Draft` with no proposal, its
  approve sets `Approved` with no approved proposal, and its import changes the English and the
  status. The new revision cannot guess every such write from the row's state: a save of the same
  text on a backfilled "Do ponownego sprawdzenia" row, or an approval again, leaves a state that
  fits the proposals. So the new revision records which rows it wrote itself, and treats every
  other write as the previous revision's.
  - **Columns** (expand, #1384; the names are suggestions). `Translations.ProposalStampAt`
    (timestamptz, nullable) holds the `UpdatedAt` that a proposal-aware revision last wrote, on a
    row that has a proposal; it is NULL on a row that never had one. `ProposalStampNeedsReview`
    (boolean, nullable) says whether the row was "Do ponownego sprawdzenia" after that write. The
    previous revision does not map them, so its writes move `UpdatedAt` and leave the stamp alone.
  - **Who sets them.**
    - The #1384 backfill, on each row it gives a proposal. It inserts the proposal and sets the
      stamp and the flag from the `UpdatedAt` and the status it read for that proposal, and it does
      not change `UpdatedAt`. Both writes read one snapshot: one statement with a data-modifying
      CTE, or a short table lock. The trap: under `READ COMMITTED`, an `UPDATE … FROM` reads again
      a row that another transaction changed in the meantime, so the `SET` must take both values
      from the snapshot that fed the insert, never from `t."UpdatedAt"` or `t."Status"`. A row the
      previous revision changes during the backfill then stays out of step and frozen, as it must.
    - `Translation` itself, in one private helper that every write method uses to set
      `UpdatedAt`. Today each write method sets `UpdatedAt` itself; #1384 adds the helper. The
      in-step test reads the row as it was loaded, before the write method changes any column.
      After the write, on a row that was in step and already has a stamp, the stamp becomes the
      new `UpdatedAt` and the flag the new status. A row with a NULL stamp gets its first stamp only
      from the write that adds its first proposal; any other write, the import included, leaves it
      NULL. On a row that was out of step, both stay as they are. In #1384 only the import writes a
      row out of step, because every other write refuses it; from #1388 the reconcile writes it
      too and sets the stamp itself.
    - The `COPY` of new rows writes NULL stamps. A new row has no Polish, so it is in step.
    - Until #1497 removes the stamp, every write to `Translations` goes through `Translation`,
      whose write methods keep the stamp. A set-based `UPDATE` of `Translations` waits for #1497:
      #1372 and #1373 depend on it. If #1421 starts after #1384 is merged and before #1497 is
      closed, it writes its backfill through `Translation` in chunks, or waits for #1497.
  - **Out of step** (one test for the domain, the link factory and #1388): the stamp is set and
    differs from `UpdatedAt`, or the stamp is NULL and `TranslatedText` is not NULL. Both columns
    get their value in one statement, so the test compares stored values. A row with no Polish and
    no proposal is never out of step, on purpose: a write of the previous revision that leaves it
    without Polish (an import) leaves nothing to reconcile.
  - **Freeze.** On a row out of step, save, approve (restore and approval again included), reject
    and withdraw answer 409 and change nothing, and bulk approve skips the row and counts it. The
    read model maps both columns, and the link factory runs the same test through one shared
    function and hides `upsert`, `approve`, `reject` and `withdraw` on such a row: a state rule
    (ADR-0040). The import still writes such a row.
  - **Accepted cost.** A rollback to the revision before #1384 freezes every row with Polish or a
    proposal that the previous revision saves, approves or imports while it runs, until #1388 is
    deployed and its reconcile has run. An import touches many rows at once. #1384 writes this into
    the runbook: keep a rollback short, and do not import during it unless it cannot wait.
  - **The contract step (#1388)** reconciles the rows out of step in a background service at API
    start, off the startup path, like `TranslationFileFormatUpgradeService`. It is idempotent. For
    each such row, with `U` = its `UpdatedAt` as read:
    1. Let `D` be the live proposal, or else the waiting one. If `TranslatedText` is not NULL and
       there is no `D`, or the text differs from `D`'s, or `SubmittedById` differs from `D`'s
       author: add a proposal with that text, author `SubmittedById`, created at `U`, origin
       "backfill" (the time is approximate). A waiting proposal becomes "replaced", and the new one
       waits.
    2. If the status as read is `Approved`, the first of these that fits applies, decided by
       `ApprovedById` at `U`: the proposal from step 1 is approved at once; or, with no live text,
       the waiting proposal whose text equals `TranslatedText` is approved (the previous revision
       approved a `Draft` row); or, when the live text equals `TranslatedText`, the flag says "Do
       ponownego sprawdzenia" and the live proposal was decided at or before the stamp, an
       "approved again after an English change" copy is added, with a link to the live proposal.
       When none applies (the live text already equals `TranslatedText` by the same author, and
       the flag does not say "Do ponownego sprawdzenia"), step 2 adds nothing.
    3. The status: `Approved` when the status as read is `Approved`. Any other row with a live text
       becomes `NeedsReview`. A row with no live text and a waiting proposal becomes `NeedsReview`
       when `PreviousSourceText` is set and (the status as read is `NeedsReview`, or the flag says
       "Do ponownego sprawdzenia"), and `Draft` otherwise. A row with no Polish keeps its status.
       `PreviousSourceText` stays as read.
    4. `TranslatedText`, `SubmittedById` and `ApprovedById` follow the proposals by the rules
       above. The stamp moves.

    So no row joins or leaves the set of `Approved` rows, and an `Approved` row keeps its text:
    `polish.txt` stays byte-identical, and the reconcile rebuilds nothing. On the safe side, what
    the final row cannot show is not recovered, and this is an accepted cost. For example: an old
    approval that a later old save replaced, the saves between, an approval again after an English
    change that the previous revision's own import made, and an old approve of a row that was
    already approved. The history then misses those approvals, although every approval is a row
    in it (#1345): the final row holds no trace of them.
  - **The deploy-window exception to the status rules.** A row that the previous revision left
    unpublished while its proposals hold a live text shows "Do ponownego sprawdzenia" until someone
    approves it again, also when its English did not change. The final row cannot show whether the
    English changed after the live proposal was approved: the previous revision's approve clears
    `PreviousSourceText`, and proposals hold no English. `Approved` could publish Polish written
    for older English under the new English's digest (ADR-0047). This keeps the wiki's rules that
    only an approved text reaches the game and that nothing comes back on its own
    (`Mechanizm-diff`).
  - **After #1388.** #1388 keeps the freeze and the stamp writes. #1497 removes them, in a release
    after #1388 runs on prod, once a read-only count of rows out of step on prod is 0 (the
    reconcile logs its errors and goes on, like its template). #1497 files the ticket that drops
    the two columns one release later (ADR-0023 §3).
  - Only #1384 writes proposals in this window. Every later ticket that writes proposals depends on
    #1388.

## Acceptance criteria

- [ ] Saving a text on an approved row leaves `polish.txt` unchanged and the row still counts as
      approved; the editor shows the live text and the waiting proposal.
- [ ] A reviewer or the admin approving someone else's waiting proposal makes it the live text and
      rebuilds `polish.txt`.
- [ ] Approving or rejecting a proposal that is no longer the waiting one returns 409 and changes
      nothing (except approve of an approved proposal other than the live one, which is a restore,
      approve of the live one on a row in "Do ponownego sprawdzenia", which is an approval again,
      and a repeat of the caller's own request that already took effect).
- [ ] A repeat of the caller's own approve, restore, approval again, reject or withdraw that
      already took effect answers 204 and changes nothing: no second history row, and a reject
      keeps its first reason.
- [ ] Neither a reviewer nor the admin can approve or reject their own waiting proposal: the button
      is not shown, and a direct call is refused. A plain translator gets 403 on approve and
      reject.
- [ ] A reviewer or the admin who wrote a text that was live restores it, or approves it again
      after an English change, without a second person.
- [ ] A save that arrives while a proposal waits replaces it; the old one shows as "replaced" in
      the history.
- [ ] Rejecting needs a reason; the translator sees the reason in the editor, with the rejected text
      ready to edit, and finds the row with the "rejected" filter. No e-mail is sent.
- [ ] A row with no live text whose last proposal was rejected shows "Odrzucone" on the list until
      the next save.
- [ ] The author withdraws their waiting proposal: it shows as "withdrawn" in the history, the row
      has no waiting proposal, and nobody else can withdraw it.
- [ ] Every save adds a proposal; no proposal is ever changed except for its outcome.
- [ ] The database refuses a second waiting proposal for one translation.
- [ ] The editor shows the full history of a row with author, origin, Polish time and outcome, to
      every signed-in translator. A history entry of a kind the editor does not know shows as a
      plain line.
- [ ] A reviewer or the admin restores an approved proposal other than the live one in one action,
      and `polish.txt` reflects it. The restore shows in the history as a new approved proposal,
      and a waiting proposal stays waiting.
- [ ] An import that changes English adds no proposals; the row goes to "Do ponownego sprawdzenia",
      its live text leaves `polish.txt`, and approving the live text again puts it back.
- [ ] Bulk approve approves only the proposals named in the request, approves again the live text
      of rows in "Do ponownego sprawdzenia", and skips and counts the caller's own waiting
      proposals.
- [ ] After the backfill, every row that had Polish, removed rows included, has exactly one
      proposal with that text and the origin "backfill", and a stamp, and `polish.txt` is
      byte-identical to the one before the migration.
- [ ] A save on a row in "Do ponownego sprawdzenia" keeps the row in that status and its old Polish
      out of `polish.txt` until an approval.
- [ ] A row in "Do ponownego sprawdzenia" that loses its last Polish (a reject or a withdraw, with
      nothing live) becomes `Untranslated`, its `PreviousSourceText` is cleared, and it shows
      "Odrzucone" after a reject.
- [ ] A save whose text ends with line breaks, on a row whose English does not, stores the proposal
      without them (#1476).
- [ ] Every row with Polish or a proposal that the previous revision wrote during the deploy or
      after a rollback, a write that changes only the status included, takes no save, approve,
      reject or withdraw from the new revision until #1388 has reconciled it (the import still
      writes it), and those links are hidden. The cases: it approves a `Draft` row whose text
      equals the waiting proposal; it saves the same text on an approved
      row; it saves the same text on a backfilled "Do ponownego sprawdzenia" row; it approves again
      a "Do ponownego sprawdzenia" row; it saves on a row with no proposal; its import touches a
      row with proposals; a write lands while the backfill runs. A row with no Polish and no
      proposal is never frozen, also after the imports of both revisions wrote it.
- [ ] After #1388, every row with Polish has a proposal for its current text, and a row the
      previous revision left unpublished is not published. An approved row whose text already
      matches its live proposal, by the same author, and that was not in "Do ponownego sprawdzenia"
      at its last stamp, gets no new proposal; an approval again by the previous revision gets its
      copy. `polish.txt` is byte-identical before and after the reconcile.
- [ ] The previous app revision keeps working against the migrated schema (the N-1 proof,
      ADR-0024).
- [ ] Account erasure leaves proposals in place under "Usunięte konto"; the data export lists the
      translator's proposals.
- [ ] Integration tests against PostgreSQL cover save, approve, reject, restore, the conflict, the
      repeats, the withdraw, the author rule, the import path, the backfill and the deploy window.
      A test that plays a write of the previous revision uses a clock that moves: a fixed clock
      would give the stamp and `UpdatedAt` the same value and hide the case.

## Open questions

### Business decisions (owner)

None open.

### Business decisions (answered)

All answers below are the owner's, given on 2026-11-13.

- **Q2. Several proposals.** Option (a): a new save replaces the waiting proposal, and the old one
  goes to the history as "replaced". It stays visible there instead of disappearing. An AI draft
  (#1373) never replaces a waiting proposal of a person: the upload skips that row.
- **Q9 (b). Restoring your own version.** Allowed. A text that was already live may be restored by
  its own author, and the author may approve their own live text again after an English change.
  The author rule covers only text that has never been live.
- **Q1. A save never changes the game file.** Yes. The live text stays in `polish.txt` until a new
  proposal is approved. The wiki rule in `Mechanizm-diff` flips first, before any code.
- **Q3. How the translator learns about a rejection.** In the app only: the editor and the
  "rejected" filter. No e-mail for now.
- **Q4. What "Zatwierdzone" means.** Option (A): reviewed by a reviewer or the admin who is not the
  author, as the wiki says (`Wersje-systemu`, the point "Tekst zatwierdza ktoś inny niż jego autor"
  under "Wersje 1.x"). No "Sprawdzone w grze" mark and no preview file.
- **Q5. Who sees the history.** Every translator sees the history of every row.
- **Q7. Withdraw.** Yes. The author may withdraw their own waiting proposal, for example after a
  save by mistake. The proposal gets the outcome "withdrawn", and `WaitingProposalId` is cleared.
- **Q8. Rejected with nothing live.** The row shows "Odrzucone" on the list until the next save.
- **Q9 (a). Who may restore.** Reviewers and the admin, not only the admin. The reason is the
  owner's principle: the admin is never the only person who can do a routine step.
- **Who may reject.** A reviewer or the admin, never the author of the text (owner decision in
  #1345, 2026-11-13; wiki `Wersje-systemu`, the point "Tekst zatwierdza ktoś inny niż jego autor"
  under "Wersje 1.x").
- **Is the reason required.** Yes: a send-back needs a reason (#1345; the wiki: "odsyła je z
  powodem").
- **Does every approve and rejection go into the history.** Yes (#1345).
- **Q6. "Zapisz i zatwierdź" for the admin.** No. It would mean approving your own new text, and
  #1368 says nobody does that, the admin included.
- **Domain model.** `TranslationProposal` is its own aggregate root (owner decision, 2026-11-13;
  see Contract → Domain).

### Changes of 2026-11-21 (#1494)

The owner decisions made after 13 November, and the gaps a review of this spec found on
2026-11-21:

- **Owner decisions this spec now follows.** The fifth status "Nie do tłumaczenia" and the admin's
  manual status change in the history (wiki `Mechanizm-diff`, #1421, #1422). The trailing line
  break rule on the domain side (#1476, 2026-11-18). The split of #1368: the Reviewer role and its
  limits first in #1488, the right to approve, reject and restore later in #1368 (2026-11-21). The
  author's right to restore and approve again a text that was already live, which the wiki
  `Wersje-systemu` states since 2026-11-21 (2026-11-13, #1368).
- **Changed from the text agreed on 2026-11-13.** A "Do ponownego sprawdzenia" row with nothing
  live and nothing waiting no longer stays in that status: it becomes `Untranslated`, because the
  wiki says that status means the Polish exists. The status section, the deploy window, the 409
  rules and the history path changed with it.
- **Design choices made in #1494, not owner decisions** (each checked twice by `/buddy`):
  - The history is one answer with more than one kind of entry, not a separate log for #1422 that
    the panel joins with the proposals: that would cost two requests per editor view and a merge
    in the client.
  - A repeat that already took effect answers 204, because a lost answer is the normal case of a
    retry. Stopping the website's retry for these writes was not taken: short server errors would
    reach the translator, who clicks again (ADR-0072, alternative I), and a double click would
    still give a 409.
  - The deploy window records which rows the new revision wrote (the stamp) instead of testing
    whether a row's text and status fit its proposals. The state test cannot see a same-text save
    on a backfilled "Do ponownego sprawdzenia" row or an approval again by the previous revision,
    and both change the history. A database trigger that marks the previous revision's writes was
    not taken: it adds hidden database logic and a later drop for a short window.
  - #1388 never publishes a row the previous revision left unpublished, so the file stays
    byte-identical and stale Polish cannot come back (see "The deploy-window exception").

### Empirical (answered)

- **Does the `||` format change?** No. One Polish text per key, as now; only the choice of text
  changes. Source: `PrecomputedTranslationFileProjector.RebuildAsync`, README "Translation file
  format".
- **Does the patcher change?** No. The digest is still computed from the current English at build
  time (ADR-0047).
- **Can a reviewer approve a text they did not see today?** Yes. `ApproveTranslation` takes only the
  row id; the `xmin` token protects the save, not what the admin saw.
- **Can a translator pull approved texts out of the game today?** Yes. `UpsertTranslation` sets
  `Draft` on an approved row and schedules a rebuild (`wasApproved`), so the row leaves the file.
- **Does an import touch Polish?** No. `TranslationDiffService` only plans the change, and
  `Translation.ApplySourceChange` changes the English and the status only.
- **Does the website retry a write?** Yes. A JSON write is sent up to two more times after a 5xx
  answer or a timeout (`HttpClientsDependencyInjectionExtensions.MayRetry`,
  `MaxRetryAttempts = 2`); only a file upload and a password confirmation are never retried.
- **Does every write of the previous revision move `UpdatedAt`?** Yes. Every write method of
  `Translation` sets it, and nothing writes `Translations` with set-based SQL today; new rows come
  through `COPY` (`BulkTranslationInserter`).

## Assumptions

- One language (Polish) and three roles (Translator, Reviewer, Admin). #1488 adds the Reviewer
  role; a reviewer may approve, reject and restore from #1368 on.
- #1368 depends on #50 and #1345, which this spec covers: the owner wants the history before more
  people can approve. So the first implementation tickets land while only the admin approves,
  rejects and restores, although the Reviewer role exists from #1488. The rules are written for
  three roles; #1368 changes the policy from Admin to Reviewer or Admin.
- Proposal rows are small (most texts are short). The growth per 1000 saves is measured once in
  #1384 and written down there.
- The column names in "The deploy window" are suggestions. #1384 may rename them; the rules stay.
- The wiki is updated with the verdicts before any code (wiki-first rule), starting with the
  `Mechanizm-diff` rule that Q1 flips. Until that change reaches prod, the wiki marks the new rule
  as coming with the ticket that ships it. ADR-0073 records the model.
