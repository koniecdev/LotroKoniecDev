# Grammar marks at the end of game texts: what they mean, and what that means for Polish (2026-11-16)

**Status:** desk research for #1370. The marks were measured on prod (read-only) and checked
against our own English rows, the Russian fan project and LOTRO Companion. **No live test yet:**
follow-up #1433 runs it on the sandbox install, then the owner decides and writes the
translator rule in the wiki. Nothing on this page is a rule yet.

Every finding has a grade:

- **PROVEN**: our own English rows or our own code show it.
- **STRONG THIRD-PARTY EVIDENCE**: another project's working practice or code shows it, but nobody
  has checked it on our client with our Polish text.
- **UNPROVEN**: a lead or a guess.

## The short answer

- **A mark is a set of one-letter flags that describes the text** for the moments when the game
  puts that text into another text through a placeholder. Other texts then pick a word by those
  flags. Our own English rows contain the article choice `#1:{ [n]| [p]| an[v]| a}`: no article
  for a name or a plural, "an" before a vowel, "a" otherwise. PROVEN.
- **The common lowercase flags**: `m` male, `f` female, `n` proper name (no article), `p` plural,
  `v` starts with a vowel sound (English "an"), `s` ends in "s" (the possessive takes only `'`).
  `e` is the mark of a text that no other flag fits. PROVEN, except `e` (a strong data pattern;
  no English text selects on it).
- **Capital letters are codes**: race codes on race names (`Dwarf[D]`), class codes on class names
  (`Warden [W]`), and `[E]`, whose meaning is UNPROVEN. The same letter means different things in
  the two lists: `Man[U]` is a race and `Guardian [U]` is a class.
- **The client reads `[m]`/`[f]` in translated text.** The Russian project patches the same
  English client and tells its translators to add `#1:{…[m]|…[f]}` choices. STRONG THIRD-PARTY
  EVIDENCE. Whether the client reads the mark of a translated *value* (`Krasnolud[D]`) is
  UNPROVEN, and it decides whether a dropped mark breaks other texts.
- **Composed player titles are built in a text we translate** (`#1 #2 #3, Apprentice Scholar`), so
  Polish can change their word order. PROVEN that the order sits in a translated text; that the
  client honours a new order is UNPROVEN. "Stout-axe Dwarf" is a text of its own; nothing shows the
  game joins "Stout-axe" and "Dwarf".
- **Polish needs what the English flags cannot give**: a gender for items and nouns (Polish
  adjectives and verbs agree with it), and three plural forms instead of two. Whether a Polish text
  can carry Polish flags is the main question for the live test.
- **Today translators copy the marks**: 233 of the 235 translated rows that end with a mark keep it
  exactly, and no row has changed a mark. That is also the Russian project's rule.

## How it was measured

- **Data:** the `translation."Translations"` table on prod, live rows only
  (`"RemovedInVersion" IS NULL`), on 2026-11-16. Read-only role through `lotro-db sql`, aggregate
  queries only, never the people tables. 792,500 live rows; 2,001 of them have Polish
  (1,808 approved, 193 drafts). A "mark" in the counts below is a bracket token at the very end of
  the English text, `\[[^][]{1,12}\]$`, unless a line says otherwise.
- **Sources** (all read on 2026-11-16):
  - The Russian project's rules page, `http://translate.lotros.ru/pages/translate-rules.html`
    (the `https` address redirects in a loop), and the picture its rule 14 links to,
    `https://preview.ibb.co/nR287x/image.png`.
  - LOTRO Companion, the `LotroCompanion` organisation on GitHub: `lotro-data` at `7097ff870cde`,
    `lotro-core` at `2356eef3c741`, `lotro-tools` at `bcda34599879`. The repos have no LICENSE,
    so this page names files and paraphrases (see
    [lotro-companion-data-model.md](lotro-companion-data-model.md)).
  - Our own code, cited as `file:line` on `origin/main` at `72ebf27a`.
- **Not done here:** anything that needs the game. No DAT was read, so the `ArgStrings` question
  below is open too.

## 1. Inventory (prod, 2026-11-16)

- **200,150 live rows end with a bracket token**, 25% of all live rows. 200,144 of them have one to
  three letters in the brackets. There are 87 different tokens, 83 of them with one to three
  letters. The ticket counted 200,146 rows and 88 marks on 2026-11-13; the content changed a
  little since.
- **Count per mark** (every mark with 100 rows or more):

| Mark | Rows | | Mark | Rows | | Mark | Rows |
|---|---:|---|---|---:|---|---|---:|
| `[e]` | 75,558 | | `[n]` | 9,740 | | `[vm]` | 335 |
| `[p]` | 22,828 | | `[s]` | 5,741 | | `[fv]` | 324 |
| `[ps]` | 22,619 | | `[fn]` | 3,564 | | `[vp]` | 243 |
| `[mn]` | 13,757 | | `[f]` | 2,782 | | `[vps]` | 232 |
| `[m]` | 13,534 | | `[mv]` | 1,548 | | `[nps]` | 191 |
| `[E]` | 12,062 | | `[sp]` | 1,419 | | `[ns]` | 180 |
| `[v]` | 12,061 | | `[vs]` | 553 | | `[nm]` | 135 |
| | | | | | | `[np]` | 107 |

- **The long tail:** 64 more tokens with 1 to 50 rows each. Most are the same letters in another
  order or case (`[mps]`, `[fps]`, `[vsp]`, `[mvn]`, `[psn]`…). The rest:
  - Race and class codes: `[L]`, `[O]` 9 each, `[U]` 7, `[D]`, `[B]` 6 each, and `[K]`, `[G]`,
    `[W]`, `[R]`, `[H]`, `[C]`, `[I]`, `[T]` 3 each (section 2).
  - Upper-case forms of the flags: `[P]` 16, `[V]` 9, `[Ps]` 8, `[MN]` 6, `[M]` 4, `[FN]` 3,
    `[N]`, `[F]`, `[Es]`, `[pN]` 1 each.
  - Developer notes: `[DNT]` on 4 rows ("do not translate").
  - Tokens that do not fit "one to three letters": `[nvps]` (four flags, 1 row), `[n=ps]`
    (1 row), and two that are plain text, not marks: `[ filter \]` (3 rows) and `[manager\]`
    (1 row).
- **Marks inside a text.** Outside `{…}` choices, a one-to-three-letter bracket token in the middle
  of a text is rare: `[max]` on 10 rows (a visible word) and `[e]` on 1 row. Inside choices,
  bracket tokens sit on 3,762 rows (section 2).
- **Translated rows.** 235 rows with Polish have an English text that ends with a mark
  (127 approved, 108 drafts). The Polish keeps the same mark on 233, changes it on 0, and drops it
  on 2 (both drafts). The ticket counted 73 such rows on 2026-11-13. 38 Polish texts contain an
  `[m]` or `[f]` somewhere.

## 2. What the marks mean

### How another text reads a mark (PROVEN from our English rows)

The English texts themselves hold small grammar programs. Three pieces of syntax:

- **A label** `#N:` in front of a placeholder names that argument: `#1:<--DO_NOT_TOUCH!-->`.
  3,360 live rows have one. `#-1:` also appears; it seems to stand for the player character
  (UNPROVEN).
- **A choice** `#N:{a[x]|b[y]|c}` prints `a` when argument N carries flag `x`, `b` when it carries
  `y`, and `c` (the branch with no mark) otherwise. 3,339 live rows have a labelled choice, and 402
  more have a `{…|…}` choice with no label.
- **Inside the brackets of a branch:** `!x` means "not x" (`#1:{the[!n]}`), `[1]` means the number
  is one (`#1:{second[1]|seconds}`), and a list means any of these (`[U,D,L]`).

Common choices in our English rows (short fragments only):

| Choice | What it shows |
|---|---|
| `#1:{ [n]\| [p]\| an[v]\| a}` | `n` and `p` take no article, `v` takes "an", the rest take "a" |
| `#1:{the[!n]}` | "the" unless the value is a name |
| `#1:{is\|are[p]}`, `#1:{it\|them[p]}` | `p` is plural |
| `#1:{he[m]\|she[f]\|it}`, `#2:{ Lord[m] \| Lady[f] }` | `m` male, `f` female, otherwise "it" |
| `#1:{'s\|'[s]}` | `s`: the value ends in "s", so the possessive is only `'` |
| `#1:{seconds\|second[1]}`, `#2:{all[!1]}` | `[1]`: the number is one; English has only one and "not one" |
| `#-1:{Skógi[D]\|Eilian}` | an NPC chosen by the player's race code |
| `#1:{None\|…[G]\|…[C]\|…[H]\|…}` | a text chosen by the player's class code |
| `#1:{ [E]}`, `#4:{ [!b]}` | a space printed only when the value has `E` / is not `b` (UNPROVEN what `E` and `b` are) |

Flags that the choices select on (bracket tokens inside `{…}`, counted per occurrence): `f` 509,
`m` 299, `E` 164, `1` 146, `O` 45, `L` 44, `D` 35, `!b` 23, `p` 17, `U` 16, the class codes about
15 each, `s` 14, `!n` 14, `v` 3, `n` 3. `e` never appears inside a choice.

**LOTRO Companion models the client the same way** (STRONG THIRD-PARTY EVIDENCE). Its renderer
fills the player name as `name[m]` or `name[f]`, and the race and class as `name[tag]`, before it
renders a `${VAR:…}` choice (`lotro-core` `RenderingUtils.setupContext`). It takes the race tag from
the trailing mark of the English race name (`lotro-tools` `RaceDataLoader`; `lotro-data`
`races.xml`: D, L, O, U, B).

### The marks are sets of flags, not words (PROVEN)

- The same letters come in any order: `[mn]` 13,757 and `[nm]` 135; `[mv]` 1,548 and `[vm]` 335;
  `[vs]` 553 and `[sv]` 21; `[fv]` 324 and `[vf]` 29; `[np]` 107 and `[pn]` 4; `[nps]` 191,
  `[psn]` 12, `[pns]` 3, `[nsp]` 1.
- A choice tests one letter: `{he[m]|she[f]|it}` serves values marked `[m]`, `[mn]`, `[mv]` and
  `[vm]` alike.
- The Russian rules page says the same (rule 19): if the letter "m" is present, the character is
  male; if "f" is present, female (`[Aragorn[mn]]`, `[Arwen[fn]]`).

### Meaning of each flag

| Flag | Meaning | Grade | Evidence |
|---|---|---|---|
| `m` | male (he, his, Lord, Hero) | PROVEN | the choices above; creature and NPC names (`Dead Man[m]`, `Saruman the White[mn]`); Russian rule 19 |
| `f` | female (she, her, Lady, Heroine) | PROVEN | the choices above; `Dun Cow[f]`, `Archivist[f]`; Russian rule 19 |
| `n` | a proper name: no "a", no "the" | PROVEN | `{ [n]\| [p]\| an[v]\| a}`, `{the[!n]}`; names and named items (`Dwalin's Shield[n]`, `Saradoc Brandybuck[mn]`) |
| `p` | plural | PROVEN | `{is\|are[p]}`, `{it\|them[p]}`; `Boots of …[p]`, `Keys of …[p]` |
| `v` | starts with a vowel sound: English "an" | PROVEN | `an[v]` above; 97.1% of the 15,536 rows with a lowercase `v` start with a vowel letter, against 6.9% of the 172,432 lowercase marks without `v`. The exceptions are mostly plurals, which take no article, and words like "Uniform" |
| `s` | ends in "s": the possessive is only `'` | PROVEN | `{'s\|'[s]}`; 98.9% of `[s]` rows end in "s" (`Treated Leather Harness[s]`), against 1.3% of `[e]` rows |
| `e` | no other flag fits: singular, consonant start, not a name, no final "s" | STRONG DATA PATTERN, meaning UNPROVEN | 75,558 rows alone, only 49 combined with another letter; 1.3% end in "s" and 1.5% start with a vowel, exactly what the other flags leave over; no choice selects on it. The Russian rules name `[e]` as a mark to keep |
| `1` | the number is one (count marks) | PROVEN for English | `{second[1]\|seconds}`; English never uses `[2]` and up. Russian translators write `#1:{день[1] \| дня[2] \| дня[3] \| дня[4] \| дней}` (picture of rule 14); whether `[2]` means "exactly 2" or "ends in 2" (22, 32…) is UNPROVEN |
| `b` | probably "blank": the value is empty | UNPROVEN | only `!b` appears, as `{ [!b]}` (a space unless the value is empty) and `{ month[!b1]\| months[!b]}` in duration texts |
| `E` | used to add a space after a title rank and before a surname | UNPROVEN | `#1:{ [E]}…#3:{ [E]}` in player title texts; Companion calls these arguments RANK and SURNAME (`${RANK: [E]}`). The same letter ends 12,062 item names (`Silver-braid Necklace[E]`), which look like the `[e]` rows (1.9% end in "s", 1.3% start with a vowel), so `E` on an item may be an older form of `e` |
| capitals in race names | race code: `U` Man and Woman, `L` Elf and High Elf, `D` Dwarf and Stout-axe, `O` Hobbit and River Hobbit, `B` Beorning | PROVEN as codes that choices select on | FileId 620756996; `#-1:{Skógi[D]\|Eilian}`; Companion `races.xml` |
| capitals in class names | class code: `R` Brawler, `G` Burglar, `C` Captain, `H` Champion, `U` Guardian, `T` Hunter, `L` Lore-master, `O` Mariner, `I` Minstrel, `K` Rune-keeper, `W` Warden, `B` Beorning | PROVEN as codes that choices select on | FileId 620756999 (note the space before the mark: `Warden [W]`); the class-choice texts |
| upper-case flags (`[P]`, `[V]`, `[MN]`, `[FN]`, `[M]`, `[N]`, `[F]`) | probably the same flags written in upper case | UNPROVEN | `Armoured Frost Troll[V]`, `Gorge Webs[P]`, `Naruhel[FN]`; the choice `#1:{ [P]\| [p]\| the }` lists both cases, a hint that case matters |
| `DNT` | a developer note: do not translate | UNPROVEN | 4 rows, all test or developer texts |

**Correction of an earlier claim.** The loop comment on #1370 of 2026-11-13 said that LOTRO
Companion removes the marks, so its German and French labels cannot show them. That is too broad.
Companion removes a mark only when a loader asks for it (`lotro-tools` `DefaultStringProcessor`,
options `OPTION_REMOVE_MARKS` and `OPTION_REMOVE_TRAILING_MARK`), and `StringUtils.fixName` cuts
only a mark that ends the text. Choices survive: `lotro-data` `lore/labels/de/factions.xml` has 10
labels with marks and `fr/factions.xml` has 13.

## 3. Does the game read the marks in Polish text?

- **A choice written into translated text: STRONG THIRD-PARTY EVIDENCE that it works.** The Russian
  project patches `client_local_English.dat`, the same English client we patch
  ([russian-project.md](russian-project.md)). Rule 14's picture shows the construction with an
  in-game screenshot: the English "Hello, `<--DO_NOT_TOUCH!-->`. You have come a long way." becomes
  `Привет, #1:<--DO_NOT_TOUCH!-->. Ты #1:{проделал[m] | проделала[f]} долгий путь.` The translator
  gives the player-name placeholder a free label and adds the choice. When the English already uses
  `#1:`, the translator takes `#2:`.
- **Our pipeline carries such a text unchanged: PROVEN.** Labels and choices are plain piece text.
  The patcher writes the pieces as they are (`PatchingService.cs:267`), and only reorders the
  argument references (`PatchingService.cs:271-278`).
- **The client reads `[m]`/`[f]` of the player name: STRONG THIRD-PARTY EVIDENCE** (the Russian
  screenshot, Companion's `RenderingUtils`).
- **The client reads the mark of a translated value (`Krasnolud[D]`, `Włamywacz [G]`): UNPROVEN.**
  If it reads the mark from the patched text, a dropped `[D]` sends a dwarf player to the default
  branch of `#-1:{Skógi[D]|Eilian}`. If it reads it from elsewhere, the mark on the value does not
  matter. The Russian rule keeps every mark, so their practice cannot tell. Live-test item b.
- **A choice in a text with no argument: UNPROVEN, and probably not.** In our English rows all
  3,339 labelled choices and all 402 unlabelled ones sit in rows that have arguments
  (`"ArgsOrder"` is NULL on none of them; the export writes NULL exactly when a fragment has no
  argument, `ExportTextsQueryHandler.cs:82-93`). The official German and French texts sometimes
  add a gender choice where English has none: key `620879993:74285333` is "Friend" in English,
  `${PLAYERNAME:Freund[m]|Freundin[f]}` in German and `${PLAYERNAME:Ami[m]|Amie[f]}` in French
  (`lotro-data` `lore/labels/{en,de,fr}/factions.xml`). Companion shows a DAT argument as
  `${VAR}` ([lotro-companion-data-model.md](lotro-companion-data-model.md)), so the German DAT
  probably has an argument there. **Our pipeline cannot add an argument**: it writes only the
  pieces, and the argument references come from the English fragment (`Fragment.cs:17-21`). Live-test
  item e.
- **`${VAR:…}` is Companion's way to show a text, never text to type.** The client syntax is
  `#N:{…}` after a labelled placeholder.

## 4. How our code treats a mark today

**Everywhere, a mark is plain text** (PROVEN):

- Patcher export joins the pieces as they are (`ExportTextsQueryHandler.cs:80`). Patch checks only
  the length of each piece (`PatchingService.cs:173-175`, `Fragment.IsWritablePiece`) and writes
  the pieces (`PatchingService.cs:267`). The escaper handles only `\`, CR and LF
  (`TranslationLineEscaper`, ADR-0039).
- The TMS stores what the translator sent (`UpsertTranslation.cs`), the file export writes it as it
  is (`TranslationFileSerializer.cs:32`, `PrecomputedTranslationFileProjector.cs:65`), and the
  editor checks only the `<--DO_NOT_TOUCH!-->` count (`PlaceholderAnalyzer.cs:11`).
- The same-text panel of #1360 matches the English exactly (`ListSameSourceTextTranslations.cs:52`),
  so `Dwarf[D]` and `Dwarf` are two different groups.

**The mark is part of the text's identity** (PROVEN):

- The patcher's `source_digest` hashes the whole text with its mark (`SourceDigest.cs:46-97`,
  ADR-0047). If SSG changes only a mark, the patcher skips the row as "source moved"
  (`PatchingService.cs:236-265`).
- The TMS `SourceHash` does the same (`SourceHash.cs:89-113`, used at `ImportExportedTexts.cs:233`),
  so a mark-only change by SSG sends a translated row to "needs review"
  (`TranslationDiffService.cs:59-80`).
- The echo hash, which recognises our own Polish coming back in an export, is computed from the
  stored Polish (`TranslationRepository.cs:70`, `SourceHash.ComputeEcho`). A design that stores the
  Polish without its mark and adds the mark only in the file breaks it: the re-imported DAT holds
  Polish with the mark, the echo no longer matches, and the row turns "needs review".

**Not reachable at all:** a fragment's `ArgStrings` are read from the DAT and written back
unchanged (`Fragment.cs:19`, `:118-176`). They are never exported and never filled from a row. If
the client keeps strings with marks there, no translator can see or change them. Live-test item d
counts them.

**What the open tickets plan, and where they differ:**

| Ticket | What it does with a mark | The definition it uses |
|---|---|---|
| #1421 (nothing to translate) | removes marks before it checks whether any letter is left | "one to three letters in square brackets", anywhere in the text (owner decision in its body) |
| #1369 (glossary) | ignores the mark when it matches a term | "the grammar mark at the end of the text" |
| #378 (identical texts) | leaves the mark out of the word count | "the grammar mark at the end" (owner decision comment, 2026-11-13) |
| #1423 (AI drafts) | checks that an answer handles marks "as the #1433 rule says" | none yet: the rule comes from #1433 |

Measured against these definitions:

- **#1421 is safe as written.** Its rule empties 3,650 untranslated rows (44 different texts)
  only because it removes a bracket token, and every token it removes there is a known flag set
  (`ps`, `e`, `p`, `E`, `es`, `s`, `ns`, `v`, `m`, `F`, `M`). No visible word in brackets gets
  erased on a row it hides. The tokens it misses (`[nvps]`, `[n=ps]`, digits) only leave a row
  visible, which is the safe direction.
- **The definitions differ in position** (anywhere vs the end) **and in shape** (one to three
  letters vs any letters). Real marks have up to four letters (`[nvps]`), upper case, and a space
  before them in the class list (`Warden [W]`).
- **#1372 (texts that differ only by a number)** groups texts by replacing digit runs with `#`.
  `#N:` labels and `[1]` count marks are digits too, but the effect is small: on 2026-11-16 no
  number-only group holds a label, and 2 groups (8 rows) hold a `[1]`.

**Recommendation** (for the product tickets after the owner's ruling): one shared definition of a
mark in the TMS Domain, used by #1369, #378, #1421 and #1423, with the position (end of text or
anywhere) as a parameter, so four tickets do not write four patterns. It stays in the TMS: the
patcher shares no code with it, and the patcher needs no mark logic.

## 5. Composed names

- **Player titles are composed in a text we translate** (PROVEN). For example, FileId 620761098
  holds a title like `#1:<--DO_NOT_TOUCH!-->#1:{ [E]}#2:<--DO_NOT_TOUCH!-->#3:{ [E]}#3:<--DO_NOT_TOUCH!-->, Apprentice Scholar`,
  and FileId 620775362 holds `… #3:<--DO_NOT_TOUCH!--> of Dale`. The order of the parts is in
  the translated text, so Polish can move the labelled parts. Whether the client honours a new
  order is UNPROVEN (live-test item c).
- **Race and sub-race names are separate texts.** The race list (FileId 620756996) has
  `Stout-axe[D]` (GossipId 49821557) next to `Dwarf[D]` (3358501). "Stout-axe Dwarf" is a text of
  its own (621025278:228870261), and so is "Get the Stout-axe Dwarf Race" (191177136). No English
  row joins the two names through placeholders. The localised labels of 49821557 differ: German
  "Stark-Axt" and Russian "Твердоруб" keep it short, French writes the whole name, "Nain des Haches
  robustes" (`lotro-data` `lore/labels/*/races.xml`). Where, if anywhere, the client shows
  "Stout-axe" and "Dwarf" together is UNPROVEN (live-test item c).
- **Names that change with the player's race or class** are choices inside one text
  (`#-1:{Skógi[D]|Eilian}`), not joined texts.

## 6. Checklist for the live test (follow-up #1433)

On the sandbox install (no UAC, [update-50/RESULTS.md](update-50/RESULTS.md)), with rows exported
from that install so their `source_digest` matches (ADR-0047). DAT reading and writing goes through
the `dat-format-expert` agent. Record what the game shows, with screenshots.

- **a. The Russian construction in Polish.** Pick a dialogue whose English has the player-name
  placeholder and no choice. Write `#2:<--DO_NOT_TOUCH!-->` and `#2:{zrobiłeś[m]|zrobiłaś[f]}` into
  the Polish. Check it with a male and with a female character.
- **b. Drop and change a race mark.** On `Krasnolud[D]`: first drop the mark, then write `[x]`.
  Each time, open a text that chooses by race (`#-1:{…[D]|…}`, for example a crafting or starter
  quest objective) with a dwarf character. Does the dwarf branch still show?
- **c. Word order.** Reorder the labelled parts of a player title (`#3:` before `#2:`) and look at
  the title in game. Find any screen that shows "Stout-axe" next to "Dwarf", and try the Polish
  order there.
- **d. `ArgStrings`.** A read-only scan of the English DAT: count the fragments with non-empty
  `ArgStrings`, show what they hold, and list a few that contain marks.
- **e. A choice with no argument.** Add `#1:{…[m]|…[f]}` to a text whose English fragment has no
  argument (`ArgsOrder` NULL). Expected: it shows as raw text or as one fixed branch.
- **f. Flags on values.** Remove `[e]`, `[v]`, `[E]` or `[ps]` on a few item names that appear in
  loot or quest messages, and see whether those messages change.
- **g. Count marks.** Write a Polish choice with `[1]`, `[2]`, `[3]`, `[4]` and a default, in a
  text with a number, and see it with the numbers 1, 2, 5, 12, 22 and 25. Does `[2]` match 22?
- **h. Polish flags.** On a value that a Polish text picks by gender (an item name or an NPC name),
  add a Polish gender letter that English does not use for that value (for example `[f]` on a
  female item name) and check that a Polish choice `{…[m]|…[f]|…}` follows it. This decides
  between options A and B below.

## 7. Proposed translator rule (a proposal, not a ruling)

**Interim rule, until the live test:**

> **Propozycja, jeszcze nie obowiązuje.**
>
> 1. Znacznik na końcu tekstu, np. `[D]`, `[mn]` albo `[ps]`, przepisz dokładnie tak jak w angielskim
>    tekście, razem ze spacją przed nim, jeśli jest. Nie tłumacz go, nie usuwaj go i nie zmieniaj
>    w nim liter.
> 2. Oznaczenia `#1:`, `#2:` przed `<--DO_NOT_TOUCH!-->` zostaw na swoim miejscu. W wyborach
>    w klamrach, np. `#1:{he[m]|she[f]|it}`, przetłumacz słowa, a znaczniki w nawiasach kwadratowych
>    przepisz bez zmian.
> 3. Formy zależnej od płci postaci nie dodawaj sam, dopóki nie sprawdzimy jej w grze.

**End state, after live-test item h.** Two options, side by side; neither leads yet:

- **Option A: keep the English marks, lock them in the editor.** The TMS stores the full text with
  its mark, and the editor shows the mark as a fixed part that the translator cannot change. Copy
  mistakes become impossible, the export and the echo hash stay as they are, and existing rows need
  no change. The cost: Polish texts can only pick by the English flags. English item names carry no
  gender, so a Polish choice that depends on the gender of an item ("Twój miecz", "Twoja tarcza")
  can never work.
- **Option B: an editable mark from a known set, checked by the editor.** The translator may change
  the mark, but only to flags from an agreed list (for example the English set plus Polish genders).
  The editor warns when a mark is dropped or is not in the list. Polish agreement becomes possible.
  The cost: translators must learn the flags, and every Polish template that uses them must be
  written with them.

Either way, the editor warning the ticket names (a mark dropped or changed) is cheap and in the
style of the placeholder check. It belongs to the product tickets after the ruling, because what
counts as "changed" depends on the option.

## 8. Open questions for the owner

These go into #1433, each with a recommended answer:

1. **Publish the interim rule (section 7) in the wiki now, before the live test?** Recommended: yes.
   233 of 235 rows already follow it, it is the Russian project's rule, and it is safe under both
   options.
2. **Option A or option B**, after live-test items b and h. Recommended: decide after the test.
   If the client reads flags from the Polish value text and accepts Polish letters, choose B; if
   not, A.
3. **The shared definition of a mark** for #1369, #378 and #1423. Recommended: "a bracket token of
   letters at the end of the text, with an optional space before it", which covers `[nvps]` and
   `Warden [W]`. Keep #1421's "one to three letters anywhere", because what it misses only leaves
   a row visible.
4. **Upper-case marks (`[P]`, `[MN]`…)**: should checks treat them as the lower-case flags?
   Recommended: no. Until the live test, a check only asks "copied exactly".

## Sources

- Our English rows: prod `translation."Translations"`, 2026-11-16 (section "How it was measured").
- Russian project rules: `http://translate.lotros.ru/pages/translate-rules.html`, rule 13 list
  ("Все встреченные в тексте вставки типа [e], [pm], [t] и т.д. должны быть сохранены."), rule 14
  and its picture `https://preview.ibb.co/nR287x/image.png`, rule 19. Read 2026-11-16.
- LOTRO Companion (read 2026-11-16 through the GitHub API):
  - `lotro-core` `src/main/java/delta/games/lotro/utils/strings/RenderingUtils.java`
    (`setupContext`), `StringUtils.java` (`fixName`, `removeMarks`).
  - `lotro-tools` `DefaultStringProcessor.java`, `RaceDataLoader.java`, `MainDatFactionsLoader.java`,
    `I18nUtils.java`.
  - `lotro-data` `lore/labels/{en,de,fr,ru}/races.xml`, `lore/labels/{en,de,fr}/factions.xml`,
    `lore/labels/en/titles.xml`, `races.xml`.
- [russian-project.md](russian-project.md), [lotro-companion-data-model.md](lotro-companion-data-model.md),
  ADR-0039, ADR-0042, ADR-0047.
