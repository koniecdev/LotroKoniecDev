# Spec 0019: Save one text in every empty row with the same English

- **Status:** Agreed
- **Date:** 2026-11-27
- **Author:** Claude Opus 5.5, from the owner decisions in #1587 (body and gate comment)
- **Ticket:** #1587
- **Related:** #378 (TP-01, the full copy feature this is the first step of), wiki `Wersje-systemu`
  ("Wersje 1.x: praca tłumaczy", 2026-11-27), ADR-0040 (amended here), ADR-0072 (amended here),
  ADR-0073 §5 and spec 0014 (writes go through `Translation`), spec 0018 (the editor's save row),
  #1347 (the panel of identical rows), #1489 (slip warnings), #1437 (placeholder count), #1384, #1421,
  #1571, #1588, #1589

## Business context

Many game texts repeat word for word in dozens or thousands of rows ("Talk to Langhár" in 41 rows,
"Wooden Spear" in 45, "Interrupted!" in 1,720). A translator who has the Polish for one of them still
opens and saves every other row by hand. On 26 November 2026 a translator pasted one text into about 40
rows and asked on Discord for a way to do it in one go. On prod, rows that repeat an identical English
text hold about 285,000 saves that a copy makes unnecessary.

## Goal

From the editor, a translator saves the typed text in this row and in every other empty row with the
same English, in one click. Each copy waits for approval like any other save.

## In scope

- A new endpoint `POST /api/v1/translations/{id}/fill-identical` with its own rel `fill-identical`.
- The count of empty identical rows and the `fill-identical` link on the panel of identical rows.
- A button „Zapisz we wszystkich identycznych (N)” in the editor's save row, with a slip check before it
  sends anything.
- One permit of the daily save budget per saved row (ADR-0072 amendment).

## Out of scope

- An automatic copy, a copy over rows that already have Polish, one approval for the whole group:
  all stay in #378.
- The button on the translation list (owner decision: the editor only).
- Holding back texts that #1421 will mark as "nothing to translate" (owner decision 1 of the gate).
- A change to „Zapisz”, „Zapisz i następny” or „Następny”.

## Owner decisions

From the ticket body (2026-11-27):

- A first step now, in 0.x, before #378.
- Copies go only into rows with no Polish text. The button never changes a row that has Polish.
- The button works for a text of any length.
- The button lives in the editor, next to the panel of identical rows.
- Each copied row counts as one save in a plain translator's daily limit. When the copy does not fit,
  the whole save is refused with the usual limit answer, and the typed text stays. The admin and a
  Reviewer have no limit.
- The copy is its own endpoint with its own link, never an option on the normal save. The rule reaches
  past this ticket, so ADR-0040 carries it as an amendment.

From the gate comment (2026-11-27):

1. Texts that #1421 will mark as "nothing to translate" are not held back. A row filled that way keeps
   its Polish, and #1421 will not give it its status. The owner accepts that.
2. The fill button checks the typed text for slips first, with the same checks as the slip warnings
   (#1489). With no slip it saves at once. With a slip it saves nothing, keeps the text, and shows the
   warnings, the line „Tekst nie jest jeszcze zapisany. Popraw go albo zapisz mimo to.” and a button
   „Zapisz we wszystkich mimo to”, which fills the rows with the text as it stands. „Zapisz” keeps
   today's behaviour until #1571 changes it.

## Business rules & edge cases

- **Identical** means the same English text and the same `ArgsOrder` and `ArgsId`: the triple that
  `SourceHash` covers. A different grammar mark is a different text (`Wooden Spear[E]` and
  `Wooden Spear[n]`). The panel's list still matches on the English only, as it does today.
- **Empty** means a live row (not removed by a game update) with no Polish. A row in „Do ponownego
  sprawdzenia” has Polish, so it is not empty.
- **N** counts every empty identical row except this one, not only the 50 rows the panel shows. The
  server finds the rows again at the moment of the save; the client never sends a list of rows.
- **This row** is saved only when the typed text differs from its stored Polish. The comparison uses
  the text as the save stores it (trailing line breaks removed for this English, #1476), compared
  exactly. So pressing the button with an unchanged text on an approved row keeps it approved, spends
  no overwrite and starts no rebuild.
- **Nothing to do.** When this row is unchanged and no empty row is left, the answer is a success with
  no write and no permit.
- **One action.** The permits, this row and every copy are one action: every row gets the text or
  none does. A parallel save on one of those rows gives the usual 409, and no row changes. A row filled
  between the page load and the press is simply not an empty row any more.
- **Checks.** The length check and the placeholder count check (#1437) run once, on the typed text.
  Every copy has the same English, so they hold for every copy. A removed row answers 422, as the save
  does.
- **Save budget (ADR-0072 amendment).** A plain translator spends one permit per saved row: N, plus one
  when this row changes. Only this row's permit can be an overwrite. When the permits do not fit, the
  answer is `Translations.SaveBudgetExhausted` (or `Translations.OverwriteBudgetExhausted`), every
  permit is handed back and no row changes. A fill that fails after its permits were taken (a 409, a
  database error) hands every permit back too.
- **Game file.** A copy never starts a rebuild: an empty row is not in the file, and a copy is not
  approved. A rebuild starts only when this row was approved and its text changed.
- **Log.** One Information line per fill: the account, the row and the number of filled rows. Its
  text starts with neither alert prefix (`Translation save budget:`, `Translation overwritten:`). The
  overwrite line is written for this row only, when it replaced Polish.

### Editor

- The button „Zapisz we wszystkich identycznych (N)” is the last button of the save row. It shows only
  when the panel carries the `fill-identical` link, and N comes from the panel. It is never in the
  „Zapisz ponownie” recovery form.
- **Slip stop.** On a press, the editor checks the typed text with the slip checker first, with no
  request. With slips it sends nothing and stays on the row: the typed text stays, the line „Tekst nie
  jest jeszcze zapisany. Popraw go albo zapisz mimo to.” sits right above the slip warnings (drawn once),
  and the save row gets „Zapisz we wszystkich mimo to”. That button carries its own `SaveAction` value,
  never a hidden field, so a later press of the plain fill button checks again. The stop is a warning
  (yellow), as #1571 draws its own stop.
- **The link.** With no slip (or after „mimo to”), the editor reads the panel and posts to the href of
  its `fill-identical` link. The fill button needs the link, so this read comes before the write; it is
  the post's only panel read. When the panel has no link any more, no empty row is left, so the rule
  above applies to this row alone: a changed text goes through the plain save, which writes what the
  fill would, and an unchanged text writes nothing. The page then shows the N = 0 line below. When the
  panel cannot be read, nothing is saved, the page says „Tekst nie jest jeszcze zapisany.” and the panel's
  own failure line shows. When the row itself cannot be loaded, nothing is saved and the recovery form
  says so. A slip stop whose panel has no link any more says „Tekst nie jest jeszcze zapisany.” and „Nie
  ma już pustych wierszy z tym samym tekstem angielskim.”, plus „Ten wiersz zapiszesz przyciskiem
  „Zapisz”.” only for a changed text: on an unchanged approved row, „Zapisz” would make a draft.
- **Success.** The editor redirects to the same row (spec 0018's address) and shows, only on that GET:
  - this row saved, N ≥ 1: „Tłumaczenie zapisano. Ten sam tekst wpisano też w N pustych wierszach z tym
    samym angielskim tekstem.” (N = 1: „w 1 pustym wierszu”);
  - this row unchanged, N ≥ 1: „Ten sam tekst wpisano w N pustych wierszach z tym samym angielskim
    tekstem. Ten wiersz już go miał, więc się nie zmienił.”;
  - N = 0: „Tłumaczenie zapisano. Nie było już pustych wierszy z tym samym tekstem angielskim.”, or,
    when nothing was written, „Nie było już pustych wierszy z tym samym tekstem angielskim. Ten wiersz
    już miał ten tekst, więc nic nie zapisano.”.
  The panel then shows the copied rows with their new Polish. A success line in the address never shows
  on a post that stays on the page.
- **Refusal.** The typed text stays. `Translations.SaveBudgetExhausted` shows „Kopia do N wierszy nie
  mieści się w dziennym limicie zapisów. Sam ten wiersz możesz zapisać przyciskiem „Zapisz”.” (N = 1:
  „Kopia do 1 wiersza…”). Every other refusal (the overwrite budget, a 409, a 400) shows its usual
  text. A server error or a timeout can come after the commit, so it adds „Nie wiadomo, czy tekst
  zapisano.” and reads the panel again; „Panel niżej pokazuje wiersze po tej próbie.” follows only when
  that read works. A failure the website made before the request left (an open circuit breaker, a
  connection that was never made) adds no such line.
- **Requests.** The website never retries a fill and never counts it toward the circuit breaker: each
  attempt would spend N + 1 permits, and a retry after a committed fill would report "no empty row
  left" for a fill that worked. It gets the long time limit of an upload (5 minutes).
- **Phone.** At 390 px every button of the save row stays inside the form with its label on one line,
  with a five-digit N and on the stop page's five buttons (spec 0018's rule).

## Contract

- **Trigger:** `POST /api/v1/translations/{id:guid}/fill-identical`, rel `fill-identical`, policy
  `RequireTranslatorRole` (the save's). The link sits on `SameSourceTextTranslationsResponse`, only when
  `EmptyIdenticalCount ≥ 1`; `ILinkFactory` replays the policy. It is not in the discovery document.
- **Input:** `FillIdenticalTranslationsRequest(string TranslatedText)`; the endpoint maps it to
  `FillIdenticalTranslations.Command(TranslationId Id, string TranslatedText)`.
- **Output:** `200` with `FillIdenticalTranslationsResponse(TranslationDetailResponse Translation, int
  FilledCount, bool TranslationSaved)`. `TranslationSaved` says whether this row was written.
- **Errors:** the save's: 400 (`Translations.Validation`,
  `TranslationEntity.TranslatedText.PlaceholderCountMismatch`), 403, 404 (`TranslationEntity.NotFound`),
  409 (a parallel change), 422 (`TranslationEntity.CannotEditRemoved`), 429
  (`Translations.SaveBudgetExhausted`, `Translations.OverwriteBudgetExhausted`).
- **Panel:** `SameSourceTextTranslationsResponse` gains `EmptyIdenticalCount` and `Links`.
- **Files touched:** none. No migration.

## Acceptance criteria

- [ ] On a row whose English is in N empty rows, the button fills all N with the typed text in one
      action, and each copy is a draft waiting for approval.
- [ ] Rows with Polish, removed rows, rows in „Do ponownego sprawdzenia”, rows with different args and
      rows with a different grammar mark never change.
- [ ] An unchanged text on an approved row keeps it approved, takes no overwrite permit, starts no
      rebuild, and fills the empty rows.
- [ ] A plain translator spends one permit per saved row; a fill that does not fit changes no row and
      leaves no permit; the admin and a Reviewer are not limited.
- [ ] A 409 changes no row and leaves no permit; a lost commit answer counts as a success.
- [ ] A row filled after the page load is not touched and the count is the real one; with no empty row
      left the row alone is saved (if changed) and the message says so.
- [ ] The game file does not change because of a copy.
- [ ] The panel's link is absent when N is 0, for a removed row and for a caller without the role.
- [ ] The editor's slip stop, „mimo to”, success, limit and "unknown" lines behave as above.
- [ ] The website never retries a fill and gives it 5 minutes.
- [ ] At 390 px the save row keeps every label on one line, with N = 14000 and on the stop page.
- [ ] A fill of about 14,000 rows is timed locally, and the time is in the PR.

## Duties for #1571

#1571 moves the slip check before „Zapisz”. This ticket already does it for the fill button. When #1571
lands, use one stop for both: the same line, the same yellow box, and „Zapisz mimo to” next to „Zapisz we
wszystkich mimo to” only when the fill link is there. The two „mimo to” values differ from `next`, `skip`,
`fill` and each other.

## Duties for #1588 and #1589

#1588 moves the normal save to the row's own address. Whichever ticket lands second keeps the shared
save steps (`TranslationSaveBudgetGate`) as the one place for the budget and the overwrite log. #1589
refuses a save when the row changed after the page was loaded: the fill takes the same check for this
row only; the copies keep this spec's rule for rows filled in the meantime.

## Duties for #1384

Each copy becomes a waiting proposal with the origin "a copy of an identical text" (spec 0014). Keep
writing every row through `Translation` (load, change, save); a set-based `UPDATE` of `Translations` is
forbidden between #1384 and #1497 (ADR-0073 §5).

## Duties for #1421

#1421 gives its status only to rows with no Polish, so a row filled by this button keeps its Polish
(owner decision 1). When #1421 lands, a row with that status is not empty: the fill query and the panel's
count must leave it out.

## Open questions

None. The owner answered both gate questions on 2026-11-27.

## Assumptions

- The sentences the ticket does not give (this row unchanged, no empty row left, the "unknown" line, the
  recovery line, the N = 1 forms) are written from the ticket's own sentences.
- With no link on the fresh panel, a changed text goes through the plain save: for this row it writes
  exactly what the fill would (a draft of the caller, one permit, an overwrite only when the row had
  Polish), and the ticket asks for this row to be saved when no empty row is left.
- The button keeps the ticket's label; only the phone CSS changes so it fits.
- The slip check also runs when the text is unchanged: the owner's answer names no exception, and „mimo
  to” keeps it from being a dead end.
- Handing permits back on a failure is for the fill only; the normal save keeps ADR-0072 §4.
