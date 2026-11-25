# ADR-0077: The Translation List Pages From a Row by an Opaque Place

**Status:** Accepted
**Date:** 2026-11-25
**Decision-makers:** Solo maintainer (ticket #1490, owner decisions of 2026-11-24 and 2026-11-25 on the
issue)
**Related:** spec 0018, `ListTranslations.cs`, `TranslationListOrder.cs`, `ListTranslationsRequest.cs`,
`TranslationListItemResponse.cs`, `PaginationLinkFactory.cs`; ADR-0040, ADR-0041, #1478, #1491, #1384,
#371

## Context

„Zapisz i następny” (#1490) must open the row that came after the saved row in the translator's list.
The list's order lives in the API: `(FileId, GossipId)` by default, and with `mine=true` the newest
change first (`UpdatedAt` descending, then `FileId`, then `GossipId`, #1491).

A save changes the very fields that decide where a row stands. It makes the row a draft, so the row
leaves the "Nieprzetłumaczone" filter, and it sets `UpdatedAt` to now, so in "Tylko moje" the row jumps
to the top. A website that searches the list again for the saved row, or that remembers the next row's
id, opens the wrong row or lands on the list after a second save of the same row, after a save with
warnings, after a double click, and after a save that empties a group of rows with the same English
(the first BLOCKED run of #1490 found all four). To find the neighbour itself, the website would also
have to copy the API's sort order.

The list had only page numbers. This is the first time a client needs "the rows after this one".

## Decision

### 1. Every list row gets an opaque place in the list's default order

`TranslationListItemResponse.Place` is a string that names where the row stands in the order the
request used. It is base64url text of a tag and the row's sort key: `(FileId, GossipId)` for the default
order, and `(UpdatedAt as UTC ticks, FileId, GossipId)` for `mine=true`. The tag says which order made
it. Clients treat it as opaque: they copy it, never read it.

It is not signed. It holds only public fields of a public row, and a changed place can only make the API
start the list somewhere else. A request with an explicit `sort` gives `Place = null`, because only the
default orders have a place.

### 2. One new list option, `after`, returns the rows after a place

`ListTranslationsRequest.After` (#1478: every option lives in the Contracts record). With `after` the API
ignores `page` and returns at most `pageSize` rows that come after the place, in the same order and with
the same filters. The response's `page` is the page that holds the first returned row at that page size,
counted from the rows that are not after the place; with no row after the place it is the last page, or
1 for an empty list. `totalCount` keeps its meaning.

Because a save never moves a row to a later place in either default order, "the first row after the old
place" is the right next row after any number of saves of the same row.

`after` with `sort`, a place the API cannot read (bad text, numbers out of range) and a place made for
the other order are refused with a 400 (`Translations.AfterWithSort`, `Translations.UnreadablePlace`).
The decoder never throws. A blank `after` counts as absent.

### 3. One class holds each order with everything that must agree with it

`TranslationListOrder` holds, for each default order: the sort, the "is after this place" filter (used
for the rows and for the page count) and the place's writer and reader. When the order changes (#1384
moves "Tylko moje" to the newest proposal), all four change in that one class. Nothing else in the API
or the website knows the order.

### 4. Links

The `self` link is the request as sent, `after` included, so it names the rows the response holds. The
first, last, previous and next links stay numbered pages without `after`: `after` is written only by the
parameterless `ListTranslationsRequest.ToQueryParameters()`, never by the page overload the paging links
use. An `after` answer is on no numbered page, so it always has the first page link, and the last
page link whenever the list has a row, also when its `page` is 1 or the last page.

## Consequences

- The website finds the next row with one list call and no knowledge of the order. "Edytuj" carries the
  row's place in the editor's address (spec 0018).
- A row that moves before the place while the translator works (another person's change in "Tylko
  moje", a conflict group that one save made agree) is skipped. The owner accepted this (spec 0018).
- An `after` request runs three queries: the total, the count of rows after the place (for the page) and
  the rows. Two counts can disagree by one row under a concurrent write, so `page` can be one off; the
  page is clamped to the pages that exist.
- The contract grows by one option and one field. The API is 0.x and every client ships from this repo,
  so no version is kept for old clients.
- #1384 must move the "Tylko moje" order inside `TranslationListOrder` (spec 0018, "Duties for #1384").
  A later default order (for example the catalog's next row, #371) adds one more order there.

## Alternatives Considered

- **Search the list again for the saved row after the save, and take its neighbour** (the ticket's first
  idea). Wrong after a second save, a save after warnings, a double click and an emptied conflict group,
  and the website would copy the API's order. Rejected.
- **Read the list before the save and remember the next row's id.** Wrong on any later save of the same
  row, and the id goes stale while others work. Rejected.
- **A "next id" in the editor's address.** It goes stale as the list changes. Rejected.
- **A dedicated "next row" endpoint.** The same mechanism with more surface; one option on the existing
  list is smaller, and the owner chose it. Rejected.
- **A per-row link "rows after this row" that the client follows.** The editor would carry an href in its
  own address, and the editor never follows an href that came with a post. Rejected.
- **The website works out the next row's page.** It cannot do that without the order. Rejected.
- **A request copy without `after` for the paging links.** Writing `after` only in the parameterless
  `ToQueryParameters()` needs no copy and still lets the contract test see every option. Rejected.
- **A `self` link that names the numbered page.** It would name rows the response does not hold.
  Rejected.
- **A signed place.** It protects nothing: the place holds no secret, and a changed place only starts a
  public list somewhere else. Rejected.

## References

- Spec 0018 (the editor's behaviour, the owner's wording and the skipped rows).
- #1490 (owner decisions of 2026-11-24 17:41 UTC and 2026-11-25).
- #1491 (the "Tylko moje" order), #1478 (query options in one Contracts record).
