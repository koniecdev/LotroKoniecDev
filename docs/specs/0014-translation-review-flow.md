# Spec 0014: Translation review flow: proposals, rejection with a reason, history

- **Status:** Agreed (2026-11-13; every owner question answered)
- **Date:** 2026-11-12, updated 2026-11-13 (owner decisions in #1345, #1368, #1369, #1372, #1373,
  the domain model, and the owner's answers to Q1–Q9)
- **Author:** koniecdev
- **Ticket:** covers #49 (TP-14 review workflow), #50 (TP-15 history), #1345 (rejection with a
  reason) and the points of #1332. Implementation tickets: #1384 (every save is a proposal) first;
  then #50 (history and restore), #1345 (rejection with a reason) and #1385 (withdraw); #1368
  (Reviewer role, the author rule) after #50. #1386 removes the wiki's "coming" mark once #1384
  runs on prod.
- **Related:** spec 0001 (status model, approve = publish), spec 0007 (bulk approve), ADR-0040
  (authorization-aware links), ADR-0047 (source digest), ADR-0065 (account erasure), ADR-0023
  (forward-only migrations), #687 (SEC-09, one hijacked translator account), #1368 (Reviewer role,
  nobody approves their own text), #1369 (glossary), #1372 (pattern saves), #1373 (AI draft
  uploads), wiki `Wersje-systemu` (1.0 criterion 4), `Mechanizm-diff` and `Import-do-TMS`.
  ADR-0073 records the domain model.

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
  approves texts, and nobody approves their own text, the admin included (#1368).
- The same people send a text back, always with a reason, and never their own text (#1345).
- Proposals will also come in bulk: one pattern save fills a whole group of rows (#1372), and the
  admin uploads AI drafts (#1373). Both must show in the history where a text came from.

## Goal

A translator can propose a text and later see whether it was approved or rejected, and why. The
text players get changes only when a reviewer or the admin approves a new one, and a text that
was never live is never approved by its own author. Every version of every text is kept, with
its author, its source and its outcome, and a reviewer or the admin can put an older version
back.

## In scope

- **Every save is a proposal.** A save no longer overwrites the text. It adds a proposal (text,
  author, time, source). Proposals are never edited or deleted.
- **The game keeps the approved text.** Each row has at most one live text: the last approved
  proposal. `polish.txt` is built from live texts. A new proposal does not change the file.
- **Approve a proposal.** A reviewer or the admin who is not the proposal's author approves one
  specific proposal (the one on their screen). It becomes the live text and the file is rebuilt.
- **Reject a proposal with a reason.** A reviewer or the admin who is not the proposal's author
  rejects one specific proposal and writes why. The reason is required. The translator sees the
  reason in the editor and can find their rejected proposals on the list. There is no e-mail (Q3).
- **Withdraw.** The author of a waiting proposal withdraws it, for example after saving by
  mistake (Q7). It goes to the history as "withdrawn", and the row has no waiting proposal any more.
- **History.** The editor shows every proposal of a row: text, author, time, source (a person's
  save, a pattern save, "szkic AI z wgrania nr N", the backfill) and outcome (waiting, approved,
  rejected with the reason, replaced, withdrawn). Every translator sees the history of every row
  (Q5).
- **Restore.** A reviewer or the admin puts an older approved proposal back (Q9 (a)), also one they
  wrote themselves, because that text was already in the game (Q9 (b)). It becomes the live text
  and the file is rebuilt. This is the undo path #50 asks for. A restore does not touch a waiting
  proposal: it stays waiting.
- **Bulk approve (spec 0007)** approves the waiting proposal of each selected row, named by its id.
  It skips a waiting proposal the caller wrote and counts it in its summary (#1368).
- **Bulk sources.** A pattern save (#1372) and an AI upload (#1373) add one proposal per row, each
  marked with its source and the save or upload it came from.
- **List and counters.** New labels and filters for "waiting for review", "change waiting" (live
  text plus a newer proposal) and "rejected". A row with no live text whose last proposal was
  rejected shows "Odrzucone" until the next save (Q8). Editing an approved text no longer lowers
  the approved counter.
- **Backfill.** Every existing row with Polish gets its current text as its first proposal, with
  the source "backfill", so the history starts with what is there today. An `Approved` row's text
  becomes an approved proposal and stays live. A `Draft` row's text becomes the waiting proposal.
  A "Do ponownego sprawdzenia" row's text becomes an approved proposal when the row has an
  approver (it was live before the English changed), otherwise the waiting proposal.

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
  older one (restore), or of the live text again after the English changed. This reverses the
  wiki rule in `Mechanizm-diff` ("Kiedy tłumacz zapisze poprawkę, wiersz wraca do statusu Wersja
  robocza"), so the wiki changes first, before any code.
- **Approve and reject name the proposal.** The request carries the proposal id the reviewer saw.
  Reject takes only the waiting proposal. Approve takes the waiting proposal, an older approved
  proposal (restore), or the live one of a row in "Do ponownego sprawdzenia" (approve again). Any
  other id (a proposal a newer save replaced, one someone else already decided, a withdrawn one)
  fails with a conflict, and nothing changes. So nobody approves a text they have not seen. Today
  this can happen: approve takes only the row id.
- **Every approval is a row in the history.** A restore, and an approval of the live text again
  after an English change, add a new proposal with that text, approved at once, whose source names
  what happened ("restore of proposal N", "approved again after an English change"). So the
  history stays append-only, an outcome is still set once, and the live text is always the last
  approved proposal.
- **Nobody approves or rejects their own text that has never been live,** the admin included
  (#1368, #1345). "Author" is the proposal's author; this replaces the `Translation.SubmittedById`
  check #1368 describes. A single approve or reject refuses it with its own error. Bulk approve
  skips it and counts it.
- **Exception (Q9 (b)): a text that was already live.** Its author may restore it, and may approve
  their own live text again after an English change. Such a text brings in nothing new.
- **Withdraw names the proposal too.** Only its author may withdraw it. If it is no longer the one
  waiting, the request fails with a conflict and nothing changes. A withdrawn proposal is never
  approved later; restore takes only proposals that were approved before.
- **The English changes (import, spec 0001).** Unchanged rule: the row becomes "Do ponownego
  sprawdzenia" and its live text leaves `polish.txt` (ADR-0047). A waiting proposal stays waiting.
  A reviewer or the admin can approve the waiting proposal, or approve the live text again ("the
  old Polish still fits"). Either clears the invalidation, as approve does today.
- **A removed row (soft removal, spec 0001)** takes no new proposals, as today. Its proposals and
  live text are kept, so a restored row comes back with them.
- **Import writes no proposals.** An import changes the English only, so the history does not grow
  with imports. (#50 feared 780k history rows per import; that cannot happen in this model.)
- **Account erasure (ADR-0065).** Proposals and rejection reasons stay, credited to the profile that
  becomes "Usunięte konto". The data export of a translator includes their proposals.
- **Dates** in the history and on rejections are shown in Polish time, like every other date.
- **Who may do what.**
  - A translator proposes, withdraws their own waiting proposal, and reads the history of every
    row.
  - A reviewer does what a translator does, approves or rejects other people's proposals, and
    restores older versions.
  - The admin does what a reviewer does, and grants or removes the Reviewer role (#1368).
  - Nobody approves or rejects their own text that has never been live (the exception for a text
    that was live is above).
  - Owner principle (2026-11-13), the reason reviewers restore too: the admin is never the only
    person who can do a routine step. In the owner's words: "Generalnie nie powinienem jako admin
    bottleneckować czegokolwiek co relatywnie mało istotne."
  - Links follow ADR-0040: `approve` and `reject` appear on a waiting proposal, and `approve` also
    on an older approved proposal (restore) and on the live text of a row in "Do ponownego
    sprawdzenia", only for a caller the policy allows. On a waiting proposal they never appear for
    its author. `withdraw` appears only on a waiting proposal, and only for its author. The role
    rule lives in the policy; the author rules are state rules (the proposal's author is or is
    not the caller), so they live in the link factory (#1368).
- **Length.** A proposal keeps the current limit (32767, ADR-0043). A rejection reason has its own
  limit: 1000 characters.

## Contract

- **Trigger:** TMS API and the editor (`/editor/{id}`), translations list (`/translations`).
- **Save:** `PUT /api/v1/translations` (`UpsertTranslation.Command`, rel `upsert`, unchanged). It
  adds a proposal. It no longer changes an `Approved` row to `Draft` and no longer schedules a
  rebuild. Response: `TranslationDetailResponse` with the live text and the waiting proposal.
- **Approve:** `POST /api/v1/translations/{id}/approve`, body `{ proposalId }`
  (`ApproveTranslation.Command(TranslationId, TranslationProposalId)`, rel `approve`, Reviewer or
  Admin). The id names the waiting proposal, an older approved proposal (restore), or the live one
  of a row in "Do ponownego sprawdzenia" (approve again). 204; 403 for a plain translator; the
  author error from #1368 when the caller wrote a waiting proposal; 409 for any other id; 422 as
  today (removed row).
- **Reject (new):** `POST /api/v1/translations/{id}/reject`, body `{ proposalId, reason }`
  (`RejectTranslationProposal.Command`, rel `reject`, Reviewer or Admin, never the proposal's
  author). 204; 400 for an empty reason or one over 1000 characters; 403 for a plain translator;
  the author error when the caller wrote the proposal; 409 when the proposal is not the one
  waiting.
- **Withdraw (new):** `POST /api/v1/translations/{id}/withdraw`, body `{ proposalId }`
  (`WithdrawTranslationProposal.Command`, rel `withdraw`, the proposal's author only). 204; 403
  when the caller is not the author; 409 when the proposal is not the one waiting; 422 as for
  approve (removed row).
- **History (new):** `GET /api/v1/translations/{id}/proposals` (`ListTranslationProposals.Query`,
  rel `history`, every signed-in translator), newest first. Restore is the `approve` rel on an
  older approved proposal, for a reviewer or the admin.
- **Bulk approve:** `POST /api/v1/translations/approve`, body `{ items: [{ id, proposalId }] }`,
  summary `{ requested, approved, skipped }` as today; a stale proposal id is skipped, and so is a
  waiting proposal the caller wrote (counted on its own, #1368).
- **Domain (owner decision, 2026-11-13): `TranslationProposal` is its own aggregate root, not a
  child of `Translation`.** ADR-0073 records it.
  - `TranslationProposal`: text, author, created at, source (a person's save, a pattern save, an
    AI draft, the backfill, a restore, an approval again after an English change) with a link to
    the pattern save (#1372), the AI upload (#1373) or the restored proposal where there is one,
    outcome, decided by, decided at, rejection reason. Proposals are only appended: the text never
    changes, and the outcome is set once (waiting → approved, rejected, replaced or withdrawn).
  - `Translation` keeps the live text (as today) and `WaitingProposalId` as plain columns. It
    enforces "at most one waiting proposal" and "approve, reject and withdraw name a proposal they
    may take, otherwise 409" itself, on its own `xmin`. `WaitingProposalId` is cleared when the
    waiting proposal is approved, rejected or withdrawn.
  - Why: `Translation` stays small and is always fully loaded. The import (`ApplySourceChange`,
    `MarkRemoved`, `Restore` through `GetByIdsAsync` in chunks of `ApplyChunkSize`) and
    `BulkApproveTranslations` load `Translation` through EF. A child collection would mean either
    an `Include` of a history that only grows, or a half-loaded aggregate where an empty list
    silently passes the rules. The bulk paths of #1372 and #1373 add proposals and check exactly
    those rules, and every later collection would add to that cost. Only new rows go through
    `COPY`.
  - The trade-off, on purpose: save, approve, reject and withdraw change two aggregates in one
    PostgreSQL transaction (one `DbContext`). The ADR records this as deliberate, so that no later
    session "fixes" it with events and eventual consistency.
  - Database guards: a foreign key from `WaitingProposalId` to the proposal, and a partial unique
    index "at most one waiting proposal per translation". The `COPY` column list in
    `BulkTranslationInserter` gets the new column; the import integration tests catch drift.
- **Files touched:** TMS database (new table, expand migration plus backfill, ADR-0023). The
  `polish.txt` artifact keeps its format; it is built from live texts. Today `TranslatedText` also
  holds a draft that nobody approved, so the migration plan must keep the previous app revision
  working while the column's meaning moves to "live text only" (ADR-0023, expand → backfill →
  contract).

## Acceptance criteria

- [ ] Saving a text on an approved row leaves `polish.txt` unchanged and the row still counts as
      approved; the editor shows the live text and the waiting proposal.
- [ ] A reviewer or the admin approving someone else's waiting proposal makes it the live text and
      rebuilds `polish.txt`.
- [ ] Approving or rejecting a proposal that is no longer the waiting one returns 409 and changes
      nothing (except approve of an older approved proposal, which is a restore).
- [ ] Neither a reviewer nor the admin can approve or reject their own waiting proposal: the button
      is not shown, and a direct call is refused. A plain translator gets 403 on approve and
      reject.
- [ ] The author of a text that was live restores it, or approves it again after an English
      change, without a second person.
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
- [ ] The editor shows the full history of a row with author, source, Polish time and outcome, to
      every signed-in translator.
- [ ] A reviewer or the admin restores an older approved proposal in one action and `polish.txt`
      reflects it. The restore shows in the history as a new approved proposal, and a waiting
      proposal stays waiting.
- [ ] An import that changes English adds no proposals; the row goes to "Do ponownego sprawdzenia",
      its live text leaves `polish.txt`, and approving the live text again puts it back.
- [ ] Bulk approve approves only the proposals named in the request, and skips and counts the
      caller's own waiting proposals.
- [ ] After the backfill, every row that had Polish has exactly one proposal with that text and the
      source "backfill", and `polish.txt` is byte-identical to the one before the migration.
- [ ] The previous app revision keeps working against the migrated schema (the N-1 proof,
      ADR-0024).
- [ ] Account erasure leaves proposals in place under "Usunięte konto"; the data export lists the
      translator's proposals.
- [ ] Integration tests against PostgreSQL cover save, approve, reject, restore, the conflict, the
      withdraw, the author rule, the import path and the backfill.

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
  author, as the wiki says (`Wersje-systemu`, criterion 4). No "Sprawdzone w grze" mark and no
  preview file.
- **Q5. Who sees the history.** Every translator sees the history of every row.
- **Q7. Withdraw.** Yes. The author may withdraw their own waiting proposal, for example after a
  save by mistake. The proposal gets the outcome "withdrawn", and `WaitingProposalId` is cleared.
- **Q8. Rejected with nothing live.** The row shows "Odrzucone" on the list until the next save.
- **Q9 (a). Who may restore.** Reviewers and the admin, not only the admin. The reason is the
  owner's principle: the admin is never the only person who can do a routine step.
- **Who may reject.** A reviewer or the admin, never the author of the text (owner decision in
  #1345, 2026-11-13; wiki `Wersje-systemu`, criterion 4).
- **Is the reason required.** Yes: a send-back needs a reason (#1345; the wiki: "odsyła je z
  powodem").
- **Does every approve and rejection go into the history.** Yes (#1345).
- **Q6. "Zapisz i zatwierdź" for the admin.** No. It would mean approving your own new text, and
  #1368 says nobody does that, the admin included.
- **Domain model.** `TranslationProposal` is its own aggregate root (owner decision, 2026-11-13;
  see Contract → Domain).

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
- **Does an import touch Polish?** No. `TranslationDiffService` changes the English and the status
  only.

## Assumptions

- One language (Polish) and three roles (Translator, Reviewer, Admin; #1368).
- #1368 depends on #50, which this spec covers: the owner wants the history before more people can
  approve. So the first implementation tickets may land while only the admin approves. The rules
  are written for three roles; each ticket applies the roles that exist when it lands.
- Proposal rows are small (most texts are short). The growth per 1000 saves is measured once during
  implementation and written down (#50 task).
- The wiki is updated with the verdicts before any code (wiki-first rule), starting with the
  `Mechanizm-diff` rule that Q1 flips. Until that change reaches prod, the wiki marks the new rule
  as coming with the ticket that ships it. ADR-0073 records the model.
