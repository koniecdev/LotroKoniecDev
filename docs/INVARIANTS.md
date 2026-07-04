# Invarianty biznesowe — LotroKoniecDev TMS

> Dokument wygenerowany przez analizę **kodu źródłowego** (warstwa Domain + serwis domenowy +
> walidatory/handlery aplikacji + konfiguracja Auth), a **nie** istniejącej dokumentacji. Celem jest
> dać wierny, weryfikowalny obraz reguł, które system faktycznie egzekwuje.
>
> Zakres: kontekst **TMS** (`src/TranslationSystem/**` + `src/AuthSystem/**`). Patcher
> (`src/LotroKoniecDev.*`) jest zamrożony i ma własny model — tutaj go nie opisujemy. Lokalizacje
> podane jako `plik:linia`. Skrót always-on: [INVARIANTS.slim.md](INVARIANTS.slim.md).

## Jak czytać ten dokument

- **Invariant** = reguła, która zawsze musi być prawdziwa w danym punkcie cyklu życia.
- Rozróżniam **gdzie** reguła jest egzekwowana:
  - 🟢 **Domena** — encja / value object / serwis domenowy (twardy invariant, nie do obejścia).
  - 🔵 **Aplikacja** — walidator FluentValidation / handler feature / konfiguracja (reguła wejścia, poza agregatem).
- **Guard vs Result:** część walidacji to `Ensure.*` / `ArgumentNullException` / `ArgumentException` —
  **rzucają wyjątek** (błędy programisty, nie biznesowe). Reszta zwraca `Result.Failure(Error)` (błąd
  dla użytkownika, mapowany na `ProblemDetails` — [API.md §5](API.md#5-error-contract-problemdetails)).
- ⚠️ = miejsce, gdzie kod może odbiegać od intuicji — pełna lista w [sekcji 12](#12-rozbieżności-i-rzeczy-warte-uwagi).
- **Walidatory są tylko dla komend** (house rule); zapytania walidują się inline w handlerze.

---

## Spis treści

0. [Konwencje przekrojowe](#0-konwencje-przekrojowe)
1. [Translation — maszyna stanów i cykl życia](#1-translation--maszyna-stanów-i-cykl-życia)
2. [Translation — Value Objects](#2-translation--value-objects)
3. [GameVersion — maszyna stanów i wartość](#3-gameversion--maszyna-stanów-i-wartość)
4. [Translator — tożsamość i prowizjonowanie](#4-translator--tożsamość-i-prowizjonowanie)
5. [PrecomputedTranslationFile — projekcja dystrybucyjna](#5-precomputedtranslationfile--projekcja-dystrybucyjna)
6. [Serwis domenowy — TranslationDiffService](#6-serwis-domenowy--translationdiffservice)
7. [Aplikacja — import](#7-aplikacja--import)
8. [Aplikacja — translations (upsert / approve / odczyty)](#8-aplikacja--translations-upsert--approve--odczyty)
9. [Aplikacja — game versions](#9-aplikacja--game-versions)
10. [Aplikacja — dystrybucja pliku](#10-aplikacja--dystrybucja-pliku)
11. [AuthSystem — uwierzytelnianie](#11-authsystem--uwierzytelnianie)
12. [Rozbieżności i rzeczy warte uwagi](#12-rozbieżności-i-rzeczy-warte-uwagi)

---

## 0. Konwencje przekrojowe

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-0.1 | 🟢 Błędy biznesowe są **wartościami**, nie wyjątkami | Reguła, która może zawieść dla użytkownika, zwraca `Result.Failure(Error)`/`Maybe.None`. `Ensure.*` / `ArgumentNullException` / `ArgumentException` rzucają — tylko dla błędów programisty. | `Translation.cs:45-48`, całe Domain |
| INV-0.2 | 🟢 Brak prymitywnej obsesji | Każdy koncept z ograniczeniem/tożsamością to `ValueObject` lub strongly-typed ID (`FragmentKey`, `TranslationSource`, `LotroNotationVersion`, `DisplayName`, `Email`, `*Id`). | Domain/Aggregates/**/ValueObjects |
| INV-0.3 | 🟢 Walidacja string-VO: kolejność **pustość → trim → długość → format** | Najpierw `IsNullOrWhiteSpace` na surowej wartości, potem `Trim()`, potem długość, potem ew. regex/gramatyka. Skutek: same spacje → `NullOrEmpty`/`InvalidFormat`, nigdy `LongerThanAllowed`. | `LotroNotationVersion.cs:34-52`, `DisplayName.cs:19-29`, `Email.cs:25-40` |
| INV-0.4 | 🟢 Strongly-typed ID-y to **GUID v7** | Wszystkie `*Id` (`TranslationId`, `GameVersionId`, `TranslatorId`, `PrecomputedTranslationFileId`, `IdentityId`) generują się przez `Guid.CreateVersion7()` (czasowo uporządkowane) i serializują jako goły string GUID. | `Primitives/**`, `SharedKernel/StronglyTypedIds` |
| INV-0.5 | 🟢 Mutacje encji z polem znacznika stemplują `UpdatedAt`/`GeneratedAt` | Metody zachowań ustawiają znacznik `now` przekazany z `TimeProvider`. `Translator` nie ma znacznika mutacji — tylko niezmienny `ProvisionedAt` (ADR-0004 aneks). | `Translation.cs`, `PrecomputedTranslationFile.cs:37` |
| INV-0.6 | 🔵 Endpointy są **autoryzowane domyślnie** | Fallback policy wymaga uwierzytelnionego użytkownika; publiczne endpointy jawnie `AllowAnonymous`. Atrybucja użytkownika brana z tokena, nigdy z body. | `ApiDependencyInjection.cs:198-219` |

---

## 1. Translation — maszyna stanów i cykl życia

**Statusy (`TranslationStatus`):** `Untranslated`, `Draft`, `Approved`, `NeedsReview` (+ `Unset`,
nigdy nieustawiany). Wiersz powstaje jako **`Untranslated`**. **Jeden enum, bez równoległej flagi
unieważnienia** — nielegalne kombinacje (np. „Approved i unieważniony") są niereprezentowalne
(spec 0001 Q6). Unieważnienie *jest* przejściem do `NeedsReview`.

### Dozwolone przejścia

| Z | Do | Metoda | Warunki / efekt |
|---|----|--------|-----------------|
| (nowy) | `Untranslated` | `CreateUntranslated` (`Translation.cs:39`) | samo angielskie źródło; stempluje `IntroducedInVersion` |
| dowolny | `Draft` | `ProvideTranslation` (`Translation.cs:118`) | dołącza polski, stempluje submittera |
| `Draft`/`NeedsReview` | `Approved` | `Approve` (`Translation.cs:136`) | wymaga polskiego + nieusunięcia; czyści `PreviousSourceText` |
| `Draft`/`Approved` | `NeedsReview` | `ApplySourceChange` (`Translation.cs:66`) | unieważnienie: zamraża `PreviousSourceText` |

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-1.1 | 🟢 `FragmentKey` (tożsamość) jest **niezmienny** | `get`-only, ustawiany tylko w konstruktorze; stabilny ponad wersjami gry. | `Translation.cs:20` |
| INV-1.2 | 🟢 Wiersz rodzi się jako `Untranslated` | Konstruktor domenowy ustawia `Status = Untranslated`. | `Translation.cs:168` |
| INV-1.3 | 🟢 `ApplySourceChange` unieważnia polski **tylko** wychodząc z `Draft`/`Approved` | Wtedy `PreviousSourceText = Source.Text` i `Status → NeedsReview`; nadpisuje `Source`, czyści miękkie usunięcie, stempluje `LastSourceChangeInVersion`. | `Translation.cs:74-83` |
| INV-1.4 | 🟢 ⚠️ `PreviousSourceText` **zamrożony** — kolejne zmiany źródła na `NeedsReview` go nie nadpisują | Wejście ponowne z `NeedsReview` nie zaklobruje bazy, względem której napisano *wciąż obecny* polski. | `Translation.cs:74` |
| INV-1.5 | 🟢 `MarkRemoved` to **miękkie** usunięcie | Ustawia `RemovedInVersion`; wiersz wykluczony z pracy i z pliku, nigdy twardo kasowany. `IsRemoved => RemovedInVersion is not null`. | `Translation.cs:90`, `:33` |
| INV-1.6 | 🟢 `Restore` zachowuje poprzedni status | Czyści tylko `RemovedInVersion`; status (w tym `Approved`) zostaje — stary polski wciąż ważny. | `Translation.cs:103` |
| INV-1.7 | 🟢 `ProvideTranslation`: **każdy** poprzedni status → `Draft` | Edycja `Approved` świadomie wyciąga wiersz z dystrybucji do ponownej akceptacji; `PreviousSourceText` nietknięty (kontekst side-by-side aż do approve). | `Translation.cs:118-126` |
| INV-1.8 | 🟢 Nie zatwierdzisz wiersza **bez polskiej treści** | `Approve` na pustym `TranslatedText` → fail. | `TranslationEntity.CannotApproveWithoutTranslation` (`Translation.cs:142`) |
| INV-1.9 | 🟢 Nie zatwierdzisz wiersza **miękko usuniętego** | `Approve` na `IsRemoved` → fail. | `TranslationEntity.CannotApproveRemoved` (`Translation.cs:147`) |
| INV-1.10 | 🟢 `Approve` czyści unieważnienie | → `Approved`, stempluje approvera, `PreviousSourceText = null` — wiersz wraca do zbioru dystrybucji. | `Translation.cs:150-155` |

---

## 2. Translation — Value Objects

| Value Object | Reguła | Dokładna wartość | Błąd |
|--------------|--------|------------------|------|
| **FragmentKey** | `FileId` dodatni, `GossipId` nieujemny; tożsamość `(FileId, GossipId)` | `FileId > 0`, `GossipId >= 0` | `TranslationEntity.FileId.Invalid` (`FragmentKey.cs:22`) / `...GossipId.Invalid` (`:30`) |
| **TranslationSource** | `Text` + `ArgsOrder` + `ArgsId` jako **jedna** wartość; `Text` niepuste (guard) | blank/`NULL` w kolumnach args → `null` | `ArgumentNullException` na `text` (programista) |

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-2.1 | 🟢 ⚠️ `GossipId == 0` jest **legalny** | Parser patchera nie nakłada dolnej granicy; tylko literał ujemny to korupcja — pojedynczy wiersz nie wywala całego importu. | `FragmentKey.cs:27-33` |
| INV-2.2 | 🟢 ⚠️ Zmiana struktury argumentów = zmiana znaczenia | Równość `TranslationSource` obejmuje `Text` **oraz** obie kolumny args, więc diff traktuje samą zmianę placeholderów jako source-change (spec 0001). | `TranslationSource.cs:45-50` |
| INV-2.3 | 🟢 `NULL`/blank w kolumnie args normalizowane do `null` | `NormalizeArgs`: `IsNullOrWhiteSpace` lub `== "NULL"` (case-insensitive) → `null`, by „brak vs NULL" nie liczył się jako zmiana. | `TranslationSource.cs:40-43` |
| INV-2.4 | 🟢 Puste źródło jest legalne i round-trippuje | Pusty fragment to poprawna treść gry; `text` tylko nie może być `null` (guard). | `TranslationSource.cs:24` |

---

## 3. GameVersion — maszyna stanów i wartość

**Statusy (`GameVersionStatus`):** `Unprocessed`, `Processed`, `Superseded` (+ `Unset`, nigdy
nieustawiany). Wersja powstaje jako **`Unprocessed`**.

| # | Invariant | Reguła | Błąd |
|---|-----------|--------|------|
| INV-3.1 | 🟢 Wersja rodzi się jako `Unprocessed` | Konstruktor ustawia `Status = Unprocessed`. | `GameVersion.cs:71` |
| INV-3.2 | 🟢 `Superseded` **nigdy** nie może być przetworzony | `MarkAsProcessed` na `Superseded` → fail; inaczej → `Processed`. | `GameVersionEntity.SupersededCannotBeProcessed` (`GameVersion.cs:28-30`) |
| INV-3.3 | 🟢 `Processed` **nie cofa się** do `Superseded` | `MarkSuperseded` na `Processed` → fail (przetworzona praca nigdy nie jest cofana). Spiętrzone `Unprocessed` są masowo oznaczane, gdy nowsza zostaje przetworzona. | `GameVersionEntity.ProcessedCannotBeSuperseded` (`GameVersion.cs:42-44`) |
| INV-3.4 | 🟢 `LotroNotationVersion`/`DetectedAt` niezmienne | `get`-only, ustawiane w konstruktorze. ⚠️ `DetectedAt` = porządek wersji (forum chronologiczne; **brak parsowania semver**). | `GameVersion.cs:13-14` |

| Value Object | Reguła | Dokładna wartość | Błąd |
|--------------|--------|------------------|------|
| **LotroNotationVersion** | niepuste, długość, gramatyka `digits(.digits)*`, **forma kanoniczna** | max **12** znaków (surowych) | `GameVersionEntity.LotroNotationVersion.NullOrEmpty` (`:34`) / `.LongerThanAllowed` (`:43`) / `.InvalidFormat` (`:48-51`) |

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-3.5 | 🟢 ⚠️ Forma kanoniczna — `48` = `48.0` = `48.0.0` | Końcowe zerowe segmenty zwijane, wiodące zera w segmencie usuwane (`047` → `47`). Zera wewnętrzne (`47.0.1`) znaczące. Równe VO. (ADR-0003) | `LotroNotationVersion.cs:77-99` |
| INV-3.6 | 🟢 ⚠️ Długość liczona na **surowym** wejściu | `>12` znaków odrzucone niezależnie od zwijania zer — forum emituje krótkie 2-3-segmentowe wersje. | `LotroNotationVersion.cs:42-46` |

---

## 4. Translator — tożsamość i prowizjonowanie

Lokalna projekcja uwierzytelnionego użytkownika (ADR-0004). `IdentityId` to jedyna cross-contextowa
referencja do AuthSystem.

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-4.1 | 🟢 `IdentityId` niezmienny, wymagany | `get`-only; `Create` wymaga `Ensure.NotEmpty(identityId)`. | `Translator.cs:21`, `:45` |
| INV-4.2 | 🟢 `RefreshProfile` zbiega profil z bieżących claimów | Odświeża `DisplayName`/`Email` (bez znacznika czasu); `null` email **czyści** wcześniejszy. Przemianowane konto zbiega bez osobnej synchronizacji. | `Translator.cs:31-37` |
| INV-4.3 | 🔵 `Translators.IdentityId` **unikalny** (klucz idempotencji) | Unikalny indeks DB gwarantuje jeden wiersz na tożsamość nawet w wyścigu. | `TranslatorConfiguration.cs:20` |
| INV-4.4 | 🔵 Prowizjonowanie **leniwe i idempotentne** przy pierwszym zapisie | `ProvisionCurrentAsync`: get-or-create po `IdentityId`; przy `DbUpdateException` (wyścig „pierwszego zapisu") odpina swój insert i re-czyta zacommitowany wiersz. | `TranslatorProvisioner.cs:40`, `:86-103` |
| INV-4.5 | 🔵 Brak tożsamości w tokenie → `Forbidden` | Gdy `MaybeIdentityId.HasNoValue`. | `Translators.Unauthenticated` (`TranslatorProvisioner.cs:43-49`) |
| INV-4.6 | 🔵 ⚠️ `DisplayName` = claim `name`, fallback `email` | Token zawsze niesie co najmniej jeden; brak obu → błąd walidacji VO. Email malformowany → po prostu brak emaila (nie wywala zapisu). | `TranslatorProvisioner.cs:55-56`, `:110-121` |

| Value Object | Reguła | Dokładna wartość | Błąd |
|--------------|--------|------------------|------|
| **DisplayName** | niepuste, max długość (trim) | **150** | `TranslatorEntity.DisplayName.NullOrEmpty` (`:19`) / `.LongerThanAllowed` (`:26`) |
| **Email** (opcjonalny) | niepuste, max długość, regex | **250**, `^[^@\s]+@[^@\s]+\.[^@\s]+$` | `TranslatorEntity.Email.InvalidFormat` (`Email.cs:27`,`:39`) / `.LongerThanAllowed` (`:34`) |

---

## 5. PrecomputedTranslationFile — projekcja dystrybucyjna

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-5.1 | 🟢 ⚠️ To `Entity`, **nie** `AggregateRoot` | Nie broni żadnego invariantu; wyprowadzony, regenerowalny. Persystowany przez `IPrecomputedTranslationFileStore` (Store ≠ Repository). (ADR-0007) | `PrecomputedTranslationFile.cs:16` |
| INV-5.2 | 🟢 Jeden wiersz na język (klucz naturalny) | Store upsertuje po `Language`; `Refresh` nadpisuje `Content`+`ContentHash` w miejscu. | `PrecomputedTranslationFile.cs:29`, `IPrecomputedTranslationFileStore.cs:12` |
| INV-5.3 | 🔵 Plik = **Approved + nieunieważnione + nieusunięte**, sort `FileId` → `GossipId` | Regenerowany po każdym zapisie zmieniającym zbiór dystrybucji (import / upsert na Approved / approve). | `PrecomputedTranslationFileProjector.cs`, `TranslationFileSerializer.cs` |
| INV-5.4 | 🔵 Kolumna `approved` zawsze `1`, terminatory CRLF | Serializacja byte-compatible z writerem patchera (golden fixture + round-trip). | `TranslationFileSerializer.cs:13-39` |

---

## 6. Serwis domenowy — TranslationDiffService

Czysta funkcja (`static`) diffująca upload względem zapisanego stanu, kluczowana po `FragmentKey`.
**Nie dotyka bazy** — handler realizuje plan po przejściu guarda truncacji.

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-6.1 | 🟢 Pięć rozłącznych wyników na wiersz | **Added** (para nieznana) · **Source-changed** (źródło różne) · **Removed** (zapisana para nieobecna) · **Restored** (usunięta para wraca z identycznym źródłem) · **Unchanged**. | `TranslationDiffService.cs:34-66` |
| INV-6.2 | 🟢 Unieważnienie liczy się tylko gdy wiersz **miał polski** | `HasPolish` = `Draft`/`Approved`/`NeedsReview`. | `TranslationDiffService.cs:80` |
| INV-6.3 | 🟢 `RemovedFraction` = 0 dla baseline | Brak aktywnych wierszy ⇒ mianownik 0 ⇒ ułamek 0; guard truncacji nigdy nie tripuje na pierwszym wczytaniu. | `TranslationDiffPlan.cs:54` |
| INV-6.4 | 🟢 `Removed` liczy tylko aktywne, nieobecne pary | `!IsRemoved && !incomingKeys.Contains(...)`. | `TranslationDiffService.cs:65-66` |

---

## 7. Aplikacja — import

`POST /api/v1/game-versions/{id}/import` (admin). Walidator komendy + handler; wszystko w jednej
transakcji (all-or-nothing, idempotentny re-upload). Błędy importu = `DataConflict` → **422**.

| # | Invariant | Reguła | Błąd |
|---|-----------|--------|------|
| INV-7.1 | 🔵 `GameVersionId` niepusty, `FileStream` nienull | Walidator komendy. | `ImportExportedTexts.cs:37-47` |
| INV-7.2 | 🔵 Nieznana wersja → `NotFound` (404) | Repo lookup przed parsowaniem. | `GameVersionEntity.NotFound` (`ImportExportedTexts.cs:90-94`) |
| INV-7.3 | 🔵 Plik z błędami parsowania odrzucony | Truncowany plik nie może udawać masowego usunięcia. | `Import.ParseFailed` (`ImportExportedTexts.cs:99`, `ImportErrors.cs:14`) |
| INV-7.4 | 🔵 Pusty upload odrzucony | Plik bez wierszy nie oznaczy wersji jako przetworzonej. | `Import.EmptyUpload` (`ImportErrors.cs:30`) |
| INV-7.5 | 🔵 Niepoprawny wiersz / zduplikowany klucz odrzuca cały import | Mapowanie do `IncomingSourceRow` z guardem unikalności `FragmentKey`. | `Import.InvalidRow` / `Import.DuplicateFragmentKey` (`ImportExportedTexts.cs:172-203`) |
| INV-7.6 | 🔵 ⚠️ Masowe usunięcie > **20%** aktywnych blokowane bez override | `RemovedFraction > MaxRemovedFractionWithoutOverride` (domyślnie `0.20`) i bez `allowMassRemoval=true` → fail. | `Import.MassRemovalBlocked` (`ImportExportedTexts.cs:125`, `ImportSettings.cs:13`) |
| INV-7.7 | 🔵 Flaga `Processed` flipuje w **tej samej** transakcji co diff | `MarkAsProcessed` + `SaveChanges` razem; potem rebuild artefaktu. | `ImportExportedTexts.cs:156-167` |

---

## 8. Aplikacja — translations (upsert / approve / odczyty)

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-8.1 | 🔵 Upsert: `FileId > 0`, `GossipId >= 0`, `TranslatedText` niepuste | Walidator komendy. | `UpsertTranslation.cs:40-52` |
| INV-8.2 | 🔵 Upsert nieistniejącego fragmentu → `NotFound` (404) | Wiersz rodzi się z importu, nie z upsertu. | `TranslationEntity.NotFound(fileId,gossipId)` (`UpsertTranslation.cs:98-102`) |
| INV-8.3 | 🔵 Upsert na miękko usuniętym → 422 | Usunięty wiersz wykluczony z pracy. | `TranslationEntity.CannotEditRemoved` (`UpsertTranslation.cs:108-111`) |
| INV-8.4 | 🔵 Upsert prowizjonuje submittera **przed** stemplowaniem FK | Pierwszy zapis tworzy `Translator` (ADR-0004). | `UpsertTranslation.cs:115-119` |
| INV-8.5 | 🔵 Edycja `Approved` wyzwala rebuild artefaktu | `wasApproved` ⇒ `RebuildAsync` po commicie (wiersz wypadł ze zbioru dystrybucji). | `UpsertTranslation.cs:123-132` |
| INV-8.6 | 🔵 Approve: `Id ≠ Empty` (walidator); nieznany → 404; prowizjonuje approvera; rebuild po sukcesie | Po `Approve` wiersz (re)wchodzi do zbioru dystrybucji ⇒ artefakt regenerowany. | `ApproveTranslation.cs:32-39`, `:76-101` |
| INV-8.7 | 🔵 Lista: nieobsługiwany `lang` → 400 (walidacja inline) | Zapytania walidują się w handlerze; dziś tylko `polish`. | `Translations.UnsupportedLanguage` (`ListTranslations.cs:56-63`) |
| INV-8.8 | 🔵 Lista **wyklucza** miękko usunięte; sort `(FileId, GossipId)`; `pageSize` clamp 1–100 | `RemovedInVersion == null`; `Page`≥1, `PageSize` clamp. | `ListTranslations.cs:38-39`, `:66`, `:87` |
| INV-8.9 | 🔵 ⚠️ Search escapuje metaznaki LIKE | Dosłowne `%`/`_` ze źródła LOTRO matchują literalnie (ILIKE, escape `\`). | `ListTranslations.cs:73-82`, `:106-110` |
| INV-8.10 | 🔵 Get-one: id `Empty` → `NotFound` (short-circuit przed DB) | Same zera nigdy nie identyfikują wiersza. | `GetTranslation.cs:43-46` |
| INV-8.11 | 🔵 Stats liczy aktywny katalog; `Translated` = Draft+Approved+NeedsReview | Jedno grupowane zapytanie; `Remaining = Total - Approved`. | `GetTranslationStats.cs:35-58` |

---

## 9. Aplikacja — game versions

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-9.1 | 🔵 Register: `Version` niepuste, ≤ **12** znaków | Walidator komendy. | `RegisterGameVersion.cs:28-35` |
| INV-9.2 | 🔵 Duplikat wersji → 422 (po formie kanonicznej) | `ExistsByVersionAsync(version)` po `LotroNotationVersion.Create` (a więc po kanonikalizacji). | `GameVersionEntity.LotroNotationVersion.AlreadyTaken` (`RegisterGameVersion.cs:74-77`) |
| INV-9.3 | 🔵 Register zwraca **201** z `Location` na item-endpoint | `/api/v1/game-versions/{id}`. | `RegisterGameVersion.cs:108-111` |
| INV-9.4 | 🔵 Lista niestronicowana, sort `DetectedAt` malejąco | Niewiele wierszy (jeden na update gry). | `ListGameVersions.cs:38-44` |
| INV-9.5 | 🔵 Get-one: id `Empty` → `NotFound` | Short-circuit przed DB. | `GetGameVersion.cs:37-39` |

---

## 10. Aplikacja — dystrybucja pliku

`GET /api/v1/translation-files/{lang}` — **anonimowy** (CLI/player pobiera).

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-10.1 | 🔵 `lang` musi być `polish` | Inaczej walidacja inline. | `TranslationFiles.UnsupportedLanguage` (400) (`GetTranslationFile.cs:39-45`) |
| INV-10.2 | 🔵 Brak zbudowanego artefaktu → 404 | Plik serwowany ze store, nigdy budowany per-request. | `TranslationFiles.NotFound` (`GetTranslationFile.cs:52-57`) |
| INV-10.3 | 🔵 ETag = content hash; `If-None-Match` → **304** | Strong tag; `*` lub dopasowanie ⇒ 304. `Cache-Control: private, no-cache`. | `GetTranslationFile.cs:76-91` |

---

## 11. AuthSystem — uwierzytelnianie

Lifted wholesale (OpenIddict + ASP.NET Identity). Pełna narracja: [auth-tutorial.md](auth-tutorial.md).

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-11.1 | 🔵 Hasło: **8–128**, ≥1 cyfra/mała/wielka/specjalny | FluentValidation; Identity `RequiredLength=8` (⚠️ bez górnego limitu na poziomie Identity). | `PasswordValidationRules.cs:13-22`, `PersistenceDependencyInjection.cs:41-45` |
| INV-11.2 | 🔵 **E-mail jest loginem** (ADR-0022): unikalny **case-insensitive** ≤ 250 regex; Username = **handle display-only**: unikalny (case-insensitive), `^[a-zA-Z0-9]+$` ≤ 150 | Walidator rejestracji + `UsernameConstants` + Identity `AllowedUserNameCharacters`; unikalność e-maila fizyczna przez **unikalny `EmailIndex`** na `NormalizedEmail`. | `RegisterUser.cs`, `UsernameConstants.cs`, `PersistenceDependencyInjection.cs`, `ApplicationUserConfiguration.cs` |
| INV-11.3 | 🔵 Zgody privacy + data-processing **muszą być true** | Inaczej walidacja rejestracji odrzuca. | `RegisterUser.cs:55-59` |
| INV-11.4 | 🔵 ⚠️ Nowy użytkownik dostaje rolę **`Translator`** | Self-register → `Translator`; seedowany admin → `Admin`. | `RegisterUser.cs:140`, `DatabaseSeederExtensions.cs:101` |
| INV-11.5 | 🔵 Email confirmation **wymagane do logowania**; lockout **5 prób / 5 min** | `SignIn.RequireConfirmedEmail`, `Lockout.MaxFailedAccessAttempts=5`. | `PersistenceDependencyInjection.cs:47-49` |
| INV-11.6 | 🔵 Tokeny: access **60 min**, refresh **14 dni** (referencyjne, rolling); email/reset **24 h** | Refresh tokeny w bazie ⇒ rewokowalne; rotacja przy użyciu. | `OpenIddictSettings.cs:16-17`, `:50`, `PersistenceDependencyInjection.cs:58` |
| INV-11.7 | 🔵 Anti-enumeration | Forgot/Resend zawsze sukces; token endpoint i strona logowania dummy-hash przy braku usera (i lockout); gate potwierdzenia e-maila **po** weryfikacji hasła. | `TokenEndpoint.cs:20-21`, `Login.cshtml.cs` |
| INV-11.8 | 🔵 ⚠️ **Brak sagi rejestracji** | Rejestracja tworzy tylko usera Auth; profil `Translator` powstaje leniwie na pierwszym zapisie TMS (ADR-0002 §7 / ADR-0004). | `RegisterUser.cs:170-173` |
| INV-11.9 | 🔵 `DeleteAccount` = erasure RODO + **permanentny lockout** | Wymaga hasła; `LockoutEnd = MaxValue`. | `DeleteAccount.cs:120-121`, `:187-191` |
| INV-11.10 | 🔵 Produkcja: auth code + **PKCE**; klucze RSA-2048 / AES-256 walidowane | Dev/Testing = klucze ephemeralne; password flow tylko w `Testing`. | `OpenIddictExtensions.cs:45`, `:113-157` |
| INV-11.11 | 🔵 **Logowanie = e-mail + hasło** na każdej ścieżce | Strona logowania i password grant robią `FindByEmailAsync`; username **nigdy nie uwierzytelnia** (charsety loginu i e-maila są rozłączne — username nie zawiera `@`). Claim `name` = username (ADR-0022). | `Login.cshtml.cs`, `TokenEndpoint.cs` |

---

## 12. Rozbieżności i rzeczy warte uwagi

- **⚠️ `PreviousSourceText` zamrożony (INV-1.4).** Kolejne zmiany źródła na `NeedsReview` nadpisują
  `Source`, ale **nie** `PreviousSourceText` — translator widzi angielski, względem którego napisano
  *wciąż obecny* polski, a nie pośredni stan, którego nigdy nie widział.
- **⚠️ `GossipId == 0` legalny (INV-2.1).** Brak dolnej granicy poza ujemnością — mirror parsera
  patchera, by jeden wiersz nie wywalił całego importu.
- **⚠️ Forma kanoniczna wersji (INV-3.5).** `48` = `48.0` = `48.0.0`. Duplikat przy rejestracji jest
  wykrywany **po** kanonikalizacji (INV-9.2), więc `48` i `48.0` kolidują.
- **⚠️ Brak parsowania semver (INV-3.4).** Porządek wersji to `DetectedAt` (forum chronologiczne), nie
  porównanie numeryczne — DAT vnum jest bezużyteczny jako wersja treści (knowledge base).
- **⚠️ Masowe usunięcie 20% (INV-7.6).** Próg domyślny, override przez `allowMassRemoval=true` —
  truncowany/częściowy export nie może udawać masowego usunięcia (spec 0001 Q4).
- **⚠️ Nowy user = `Translator`, nie `User` (INV-11.4).** Role to `Admin`/`Translator` (nie
  KittySaverowe `Admin`/`User`/`Moderator`).
- **⚠️ Brak sagi rejestracji (INV-11.8).** Świadomy non-lift — `Translator` prowizjonowany leniwie.
- **Jeden enum zamiast bool+flaga.** `TranslationStatus` i `GameVersionStatus` kodują pełny cykl życia
  w jednym enumie — nielegalne kombinacje niereprezentowalne (spec 0001 Q6).
- **Projekcja ≠ agregat (INV-5.1).** `PrecomputedTranslationFile` to `Entity` za `Store`, nie agregat
  za `Repository` — celowo (ADR-0007).
