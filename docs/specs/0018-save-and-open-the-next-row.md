# Spec 0018: Save and open the next row

- **Status:** Agreed
- **Date:** 2026-11-25
- **Author:** Claude Opus 5.5, from the owner decisions on #1490
- **Ticket:** #1490
- **Related:** ADR-0077 (the place and `after` contract), #1452 (the list state in the editor's address),
  #1489 (the warnings), #1493 (the list's inline save), #1384 (proposals, which changes the "Tylko moje"
  order), #1569 (a second click in the browser), ADR-0039 §6 and #1476 (line breaks at the end),
  ADR-0040, ADR-0041, ADR-0044, ADR-0072

## Business context

A translator works through a list: a search, a status filter, page after page. After every save they go
back to the list and open the next row by hand. With #1452 the way back keeps the list, but it is still
two clicks and a page load for every row, hundreds of times a day.

## Goal

From the editor, the translator saves a row and lands on the next row of the same list in one click.

## In scope

- A second button, „Zapisz i następny”, next to „Zapisz” in the editor.
- A "place" for each row of the translation list and a new list option, `after`, that returns the rows
  after a place (ADR-0077).
- One shared rule for removing line breaks at the end of the Polish.

## Out of scope

- The list's inline save (#1493) gets no "next".
- The recovery form, which the editor shows when the row cannot load, keeps only „Zapisz ponownie”.
- Blocking a second click in the browser: #1569.
- The catalog's own "next untranslated" (#371, M7).

## Owner decisions

From the comments on #1490 of 2026-11-24 17:41 UTC and 2026-11-25:

- „Zapisz” keeps working as today: it saves and stays on the row.
- „Zapisz i następny” opens the row that came after the saved one in the same list: the same search,
  match, status, „Tylko rozbieżne”, „Tylko moje” and page size. After the last row of a page it opens the
  first row of the next page. After the last row of the whole list it goes back to the list.
- The list API gives each row an opaque place in its own order, and gets one new option, "rows after
  this place". The API also says which page the next row is on (Q1 (a)).
- A row opened with „Otwórz” from the panel of rows with the same English has no „Zapisz i następny”
  (Q2).
- An unchanged text is not saved: the button just opens the next row (Q4). "Unchanged" means that the
  field equals the text the form showed, after the save's trimming of line breaks at the end (decision
  of 2026-11-25). An empty field on an untranslated row stays a failed save.
- The wording (Q3):
  - On the next row: „Poprzednie tłumaczenie zapisano.”
  - On the list after the last row: „Tłumaczenie zapisano. To był ostatni wiersz tej listy.”
  - When the save worked but the next row cannot be read: the translator stays on the saved row with
    „Tłumaczenie zapisano.” and „Nie udało się otworzyć następnego wiersza. Spróbuj jeszcze raz albo
    wróć do listy.”
  - None of these lines shows after a failed save.

## Business rules & edge cases

- **The next row** is the first row after the saved row's place, read after the save. A save never moves
  a row to a later place in either default order (a save sets the change time to now, and "Tylko moje"
  shows the newest change first), so a second save of the same row, a save after warnings, „Zatwierdź”
  and then the button, or two posts one after the other all open the same next row.
- **Only „Edytuj” on the list writes a place** into the editor's address. A bare `/editor/{id}`, a row
  opened with „Otwórz”, and a row opened with „Otwórz w edytorze” from the note after an inline save
  show no button: none of them has a place in a list (Q2's reason). Page 1 of the unfiltered list does
  show it.
- „Zapisz” and „Zatwierdź” keep the place in the address, so the button stays after them.
- **A changed text** is saved as „Zapisz” saves it (the same API call, the same save budget, the same
  checks). A failed save stays on the row with the error and the typed text. A good save whose text has
  warnings (#1489) stays on the row with „Tłumaczenie zapisano.” and the warnings, so the translator can
  fix the slip. A second press with no change then moves on.
- **An unchanged text** is not sent. It spends nothing from the save budget and writes nothing.
  - The field is compared with the text the form showed, which the form carries in a hidden field. After
    a failed save the hidden field keeps the text first shown, so the next press still compares with it.
  - Both texts go through the save's own trimming of line breaks at the end for this English.
  - A blank shown text (an untranslated row) never counts as unchanged, so an empty field reaches the save
    and its refusal.
  - When the row cannot load during the press, the English is unknown, so the comparison is exact.
- **A text that starts with a line break.** The browser drops one line break right after a `<textarea>`
  start tag. The editor's save form therefore always writes one line break before the text it shows, so
  the field holds the stored text exactly, and an untouched row compares as unchanged. This also stops
  „Zapisz” from dropping that first line break. The other option, cutting the line break from the hidden
  value instead, was rejected: it copies the browser's rule into our code, it keeps showing the text
  without its first line break, and an edit still loses it.
- **After the last row** the editor goes back to the list with its state, on the page the API names: the
  last page that exists, or page 1 when the list is empty now. The list shows „Tłumaczenie zapisano. To
  był ostatni wiersz tej listy.” after a save, and only „To był ostatni wiersz tej listy.” after an
  unchanged press. The note shows on an empty page and when the list cannot load too.
- **The next row cannot be read** (the API is down, or it refuses the place): the editor stays on the
  row with „Tłumaczenie zapisano.” (only when this press saved) and „Nie udało się otworzyć następnego
  wiersza. Spróbuj jeszcze raz albo wróć do listy.”
- **No "zapisano" line after an unchanged press**, because nothing was saved: the next row shows no line,
  the list shows only „To był ostatni wiersz tej listy.”, and a failed next read shows only the „Nie
  udało się otworzyć…” line.
- **The lines survive a failed load.** When the next row, or the list, cannot load after the redirect,
  the page still shows the line that says the save went through (as #1122 does for „Zapisz”).
- **Rows that are skipped and never revisited** (owner decision, accepted):
  - With „Tylko rozbieżne”, a save can make a whole group of rows with the same English agree, so the
    group leaves the filter. Its rows after the saved one are not offered.
  - In „Tylko moje”, a row that another person changes while the translator works moves to the top of
    the list, before the place, so it is not offered again.
- **A fast double click.** Two posts one after the other open the same next row; the second one saves
  again only when the text changed, and spends one more permit. A truly simultaneous double click can
  answer the second post with an error (409) although the first save went through, as „Zapisz” does
  today. #1569 stops the common case in the browser.

## Contract

- **Trigger:** the editor's save form at `/editor/{id}?…&place=<place>`, with the pressed button's name
  `SaveAction` and value `next`; „Zapisz” sends no value.
- **List API:** `GET /api/v1/translations` (`ListTranslationsRequest`):
  - Each item of `TranslationListItemResponse` has `place`, an opaque string, or `null` when the
    request has a `sort`.
  - New option `after=<place>`: the rows after that place in the list's default order, at most
    `pageSize` of them. `page` is ignored on the way in. On the way out, `page` is the page of the first
    returned row at that page size, or the last page (at least 1) when no row follows. `totalCount` is
    the size of the whole filtered list, as without `after`.
  - A blank `after` counts as absent.
  - `after` with `sort`, a place the API cannot read, or a place made for the other order → 400
    `Translations.AfterWithSort` or `Translations.UnreadablePlace`, never a 500.
  - The `self` link is the request as sent, `after` included. The first, last, previous and next links
    are numbered pages without `after`.
- **Editor addresses:** the list state as in #1452, plus `place`. The new flags are
  `previousSaved=true`, `nextFailed=true`, and on the list `lastRow=saved` or `lastRow=unchanged`. Each is
  read as text, so a bad value shows the page without the line, and only on a GET.

## Acceptance criteria

- [ ] „Zapisz i następny” saves a changed text and opens the next row of the same list, also across a
      page break. „Wróć do listy” on that row opens the page the API names.
- [ ] After the last row it returns to the list with its state, on the last page that exists, with the
      last-row note (also on an empty page and when the list cannot load).
- [ ] A saved text with warnings keeps the translator on the row; a second press with no change moves
      on.
- [ ] „Zapisz” still saves and stays on the row, and keeps the place.
- [ ] A failed save stays on the row with the error and the typed text, and shows no new line.
- [ ] An unchanged text is not saved and opens the next row; an empty field on an untranslated row is
      still refused.
- [ ] The next row follows the saved one after a second save, after warnings, after „Zatwierdź”, after
      two posts one after the other, when the save took the row out of the filter, and in „Tylko moje”.
- [ ] „Poprzednie tłumaczenie zapisano.” shows only when this press saved.
- [ ] A failed next read keeps the translator on the row with the two lines.
- [ ] No button without a place, after „Otwórz”, after „Otwórz w edytorze” or on a read-only row.
- [ ] The API refuses a bad `after` with a 400, never a 500.

## Duties for #1384

When every save becomes a proposal (#1384), "Tylko moje" moves to the newest proposal (spec 0014). Then:

1. Move the "Tylko moje" order in `TranslationListOrder` with the new order: the sort, the "after"
   filter and the place change together in that one class (ADR-0077).
2. Check that the save form's hidden field still carries the text the form shows (the waiting proposal),
   so an unchanged press still skips the save.
3. A reject or withdraw (#1345, #1385) moves a row's "Tylko moje" time back to an older proposal, so the
   row moves later in the list and can be offered again as "next". Name it as a third kind of row in this
   spec, or keep it out of the order.

## Open questions

None. The owner answered every question on #1490.

## Assumptions

- The wording after an unchanged press (no "zapisano" line) is built from the owner's own sentences
  without the part that would be false.
- Every caller of the list gets a place, visitors included: it holds only the row's public ids and
  change time, so it needs no signature.
