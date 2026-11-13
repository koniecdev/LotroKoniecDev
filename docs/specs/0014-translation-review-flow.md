# Spec 0014: Translation review flow: proposals, rejection with a reason, history

- **Status:** Draft
- **Date:** 2026-11-12
- **Author:** koniecdev
- **Ticket:** covers #49 (TP-14 review workflow), #50 (TP-15 history) and the points of #1332.
  Implementation tickets are cut from this spec once it is Agreed.
- **Related:** spec 0001 (status model, approve = publish), spec 0007 (bulk approve), ADR-0040
  (authorization-aware links), ADR-0047 (source digest), ADR-0065 (account erasure), ADR-0023
  (forward-only migrations), #687 (SEC-09, one hijacked translator account), wiki `Mechanizm-diff`
  and `Import-do-TMS`. A new ADR records the model once the spec is Agreed.

## Business context

The first outside translator started working on 2026-11-11. Two gaps showed up on the first day:

- The reviewer cannot send a text back. A translator wrote "Wysoki elf" for "High Elf"; the agreed
  term is "Elf wysokiego rodu". Today the admin can only approve it or overwrite it, and the
  translator never learns why.
- A save overwrites the text in place. Nothing keeps the previous text, so a mistake or a vandal
  cannot be undone (#50), and the translator cannot see what was there before (#1332).

A third problem is less visible: when a translator edits an approved text, the row becomes a draft
and leaves `polish.txt` until the admin approves it again. Players would see English in the
meantime, and the approved counter drops. It also means any translator account can pull approved
texts out of the game just by editing them (#687).

## Goal

A translator can propose a text and later see whether it was approved or rejected, and why. The
text players get changes only when an admin approves a new one. Every version of every text is kept,
with its author and its outcome, and an admin can put an older version back.

## In scope

- **Every save is a proposal.** A save no longer overwrites the text. It adds a proposal (text,
  author, time). Proposals are never edited or deleted.
- **The game keeps the approved text.** Each row has at most one live text: the last approved
  proposal. `polish.txt` is built from live texts. A new proposal does not change the file.
- **Approve a proposal.** The admin approves one specific proposal (the one on their screen). It
  becomes the live text and the file is rebuilt.
- **Reject a proposal with a reason.** The admin rejects one specific proposal and writes why. The
  translator sees the reason in the editor and can find their rejected proposals on the list.
- **History.** The editor shows every proposal of a row: text, author, time, outcome (waiting,
  approved, rejected with the reason, replaced).
- **Restore.** The admin approves an older proposal again. It becomes the live text and the file is
  rebuilt. This is the undo path #50 asks for.
- **Bulk approve (spec 0007)** approves the waiting proposal of each selected row, named by its id.
- **List and counters.** New labels and filters for "waiting for review", "change waiting" (live
  text plus a newer proposal) and "rejected". Editing an approved text no longer lowers the
  approved counter.
- **Backfill.** Every existing row with Polish gets its current text as its first proposal, so the
  history starts with what is there today.

## Out of scope

- Glossary (#385).
- E-mail or other push notifications (unless Q3 says otherwise).
- Discussion threads. A rejection carries one reason; there are no replies.
- Bulk reject.
- A preview file with waiting proposals for testing in the game (unless Q4 picks option C).
- AI review (#51), per-language roles.
- Any change to the `||` file format or the patcher.

## Business rules & edge cases

- **A save never changes what players get.** Only an approve does: of a new proposal, of an older
  one (restore), or of the live text again after the English changed.
- **Approve and reject name the proposal.** The request carries the proposal id the admin saw. If
  that proposal is no longer the one waiting (a newer save replaced it, or another admin already
  decided), the request fails with a conflict and nothing changes. So an admin never approves a
  text they have not seen. Today this can happen: approve takes only the row id.
- **The English changes (import, spec 0001).** Unchanged rule: the row becomes "Do ponownego
  sprawdzenia" and its live text leaves `polish.txt` (ADR-0047). A waiting proposal stays waiting.
  The admin can approve the waiting proposal, or approve the live text again ("the old Polish
  still fits"). Either clears the invalidation, as approve does today.
- **A removed row (soft removal, spec 0001)** takes no new proposals, as today. Its proposals and
  live text are kept, so a restored row comes back with them.
- **Import writes no proposals.** An import changes the English only, so the history does not grow
  with imports. (#50 feared 780k history rows per import; that cannot happen in this model.)
- **Account erasure (ADR-0065).** Proposals and rejection reasons stay, credited to the profile that
  becomes "Usunięte konto". The data export of a translator includes their proposals.
- **Dates** in the history and on rejections are shown in Polish time, like every other date.
- **Who may do what.** A translator proposes. An admin proposes, approves, rejects and restores.
  Links follow ADR-0040: `approve` and `reject` appear only on a proposal that is waiting (or on
  the live text of a row in "Do ponownego sprawdzenia"), and only for a caller allowed to follow
  them.
- **Length.** A proposal keeps the current limit (32767, ADR-0043). A rejection reason has its own
  limit (proposed: 1000 characters).

## Contract

- **Trigger:** TMS API and the editor (`/editor/{id}`), translations list (`/translations`).
- **Save:** `PUT /api/v1/translations` (`UpsertTranslation.Command`, rel `upsert`, unchanged). It
  adds a proposal. It no longer changes an `Approved` row to `Draft` and no longer schedules a
  rebuild. Response: `TranslationDetailResponse` with the live text and the waiting proposal.
- **Approve:** `POST /api/v1/translations/{id}/approve`, body `{ proposalId }`
  (`ApproveTranslation.Command(TranslationId, TranslationProposalId)`, rel `approve`). 204;
  409 when the proposal is not the one waiting; 422 as today (removed row).
- **Reject (new):** `POST /api/v1/translations/{id}/reject`, body `{ proposalId, reason }`
  (`RejectTranslationProposal.Command`, rel `reject`, admin only). 204; 400 for an empty or too
  long reason; 409 when the proposal is not the one waiting.
- **History (new):** `GET /api/v1/translations/{id}/proposals` (`ListTranslationProposals.Query`,
  rel `history`), newest first. Restore is the `approve` rel on an older approved proposal.
- **Bulk approve:** `POST /api/v1/translations/approve`, body `{ items: [{ id, proposalId }] }`,
  summary `{ requested, approved, skipped }` as today; a stale proposal id is skipped.
- **Domain:** a new `TranslationProposal` (text, author, created at, outcome, decided by, decided
  at, rejection reason). The ADR decides whether it is a child entity of `Translation` or its own
  aggregate. The bulk import paths load `Translation` rows in large batches, which argues for its
  own aggregate.
- **Files touched:** TMS database (new table, expand migration plus backfill, ADR-0023). The
  `polish.txt` artifact keeps its format; it is built from live texts.

## Acceptance criteria

- [ ] Saving a text on an approved row leaves `polish.txt` unchanged and the row still counts as
      approved; the editor shows the live text and the waiting proposal.
- [ ] Approving the waiting proposal makes it the live text and rebuilds `polish.txt`.
- [ ] Approving or rejecting a proposal that is no longer the waiting one returns 409 and changes
      nothing.
- [ ] Rejecting needs a reason; the translator sees the reason in the editor, with the rejected text
      ready to edit, and finds the row with the "rejected" filter.
- [ ] Every save adds a proposal; no proposal is ever changed except for its outcome.
- [ ] The editor shows the full history of a row with author, Polish time and outcome.
- [ ] An admin restores an older approved proposal in one action and `polish.txt` reflects it.
- [ ] An import that changes English adds no proposals; the row goes to "Do ponownego sprawdzenia",
      its live text leaves `polish.txt`, and approving the live text again puts it back.
- [ ] Bulk approve approves only the proposals named in the request.
- [ ] After the backfill, every row that had Polish has exactly one proposal with that text, and
      `polish.txt` is byte-identical to the one before the migration.
- [ ] Account erasure leaves proposals in place under "Usunięte konto"; the data export lists the
      translator's proposals.
- [ ] Integration tests against PostgreSQL cover save, approve, reject, restore, the conflict, the
      import path and the backfill.

## Open questions

### Business decisions (owner)

- **Q1. A save never changes the game file.** The live text stays in `polish.txt` until a new
  proposal is approved. This reverses the wiki rule "Kiedy tłumacz zapisze poprawkę, wiersz wraca
  do statusu Wersja robocza" (`Mechanizm-diff`). Agree?
- **Q2. Several proposals.** When a second save arrives while one proposal waits: (a) the new one
  replaces it, and the old one goes to history as "replaced"; or (b) both wait side by side and the
  admin picks one. With one active translator (a) is enough, and the model can grow into (b) later.
- **Q3. Rejection.** Is the reason required? How does the translator learn about it: only in the
  app (editor + "rejected" filter), or by e-mail too?
- **Q4. What "Zatwierdzone" means.** (A) reviewed by the admin, as the wiki says today; (B) as (A),
  plus an optional "Sprawdzone w grze" mark that does not affect the file; (C) approval requires an
  in-game check, which needs a preview file with waiting proposals for the reviewer's own game (new
  artifact and a CLI option, a large addition).
- **Q5. Who sees the history.** Every translator, or only the admin and the author? Restore stays
  admin-only.
- **Q6. Admin's own fix.** Should an admin get one button, "Zapisz i zatwierdź", for a fix like
  "Elf wysokiego rodu", instead of save and then approve?
- **Q7. Withdraw.** May a translator withdraw their own waiting proposal? (A new save already
  replaces it under Q2 (a).)
- **Q8. Rejected with nothing live.** On the list, does such a row show "Odrzucone" until the next
  save, or go back to "Nieprzetłumaczone"?

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

- One language (Polish) and two roles (Translator, Admin), as today.
- Proposal rows are small (most texts are short). The growth per 1000 saves is measured once during
  implementation and written down (#50 task).
- The wiki is updated with the verdicts before any code (wiki-first rule), and an ADR records the
  model.
