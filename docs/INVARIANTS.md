# Invarianty biznesowe — LotroKoniecDev TMS

> **Jedyny katalog egzekwowanych reguł biznesowych TMS** — wygenerowany przez analizę **kodu
> źródłowego** (warstwa Domain + serwis domenowy + walidatory/handlery aplikacji + konfiguracja
> Auth), a **nie** istniejącej dokumentacji. Celem jest dać wierny, weryfikowalny obraz reguł, które
> system faktycznie egzekwuje. (Dawny skrót `INVARIANTS.slim.md` usunięto 2026-07-11 — był
> redundantnym, ręcznie utrzymywanym duplikatem tego pliku, nigdzie niereferowanym.)
>
> Zakres: kontekst **TMS** (`src/TranslationSystem/**` + `src/AuthSystem/**`). Patcher
> (`src/Patcher/**`) jest stabilny (już nie „zamrożony" — ADR-0002 aneks 2026-06-25) i ma własny
> model — tutaj go nie opisujemy. Lokalizacje podane jako `plik:linia`.

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
| INV-0.5 | 🟢 Mutacje encji z polem znacznika stemplują `UpdatedAt`/`GeneratedAt` | Metody zachowań ustawiają znacznik `now` przekazany z `TimeProvider` (dla projekcji: set-based refresh w store stempluje `GeneratedAt`). `Translator` nie ma znacznika mutacji — tylko niezmienny `ProvisionedAt` (ADR-0004 aneks). | `Translation.cs`, `IPrecomputedTranslationFileStore.cs:16-21` |
| INV-0.6 | 🔵 Endpointy są **autoryzowane domyślnie** | Fallback policy wymaga uwierzytelnionego użytkownika; publiczne endpointy jawnie `AllowAnonymous` (dystrybucja pliku, `GET /progress`, read-only lista tłumaczeń — #309). Atrybucja użytkownika brana z tokena, nigdy z body. | `ApiDependencyInjection.cs:258-279` |

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
| INV-3.1 | 🟢 Wersja rodzi się jako `Unprocessed` | Konstruktor ustawia `Status = Unprocessed`. | `GameVersion.cs:87` |
| INV-3.2 | 🟢 `Superseded` **nigdy** nie może być przetworzony | `MarkAsProcessed` na `Superseded` → fail; inaczej → `Processed` (re-upload do `Processed` legalny i idempotentny). | `GameVersionEntity.SupersededCannotBeProcessed` (`GameVersion.cs:28-30`) |
| INV-3.3 | 🟢 `Processed` **nie cofa się** do `Superseded` | `MarkSuperseded` na `Processed` → fail (przetworzona praca nigdy nie jest cofana). Spiętrzone `Unprocessed` są masowo oznaczane, gdy nowsza zostaje przetworzona. | `GameVersionEntity.ProcessedCannotBeSuperseded` (`GameVersion.cs:42-44`) |
| INV-3.4 | 🟢 `LotroNotationVersion`/`DetectedAt` niezmienne | `get`-only, ustawiane w konstruktorze. ⚠️ `DetectedAt` = porządek wersji (chronologia rejestracji; **brak parsowania semver**). | `GameVersion.cs:13-14` |
| INV-3.7 | 🟢 Tylko `Unprocessed` może być **usunięta** | `EnsureCanBeDeleted` na `Processed`/`Superseded` → fail — wersja wpleciona w cykl aktualizacji (spec 0001). Warunek cross-agregatowy w handlerze (INV-9.6). | `GameVersionEntity.OnlyUnprocessedCanBeDeleted` (`GameVersion.cs:58-66`) |

| Value Object | Reguła | Dokładna wartość | Błąd |
|--------------|--------|------------------|------|
| **LotroNotationVersion** | niepuste, długość, gramatyka `digits(.digits)*`, **forma kanoniczna** | max **12** znaków (surowych) | `GameVersionEntity.LotroNotationVersion.NullOrEmpty` (`:34`) / `.LongerThanAllowed` (`:43`) / `.InvalidFormat` (`:48-51`) |

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-3.5 | 🟢 ⚠️ Forma kanoniczna — `48` = `48.0` = `48.0.0` | Końcowe zerowe segmenty zwijane, wiodące zera w segmencie usuwane (`047` → `47`). Zera wewnętrzne (`47.0.1`) znaczące. Równe VO. (ADR-0003) | `LotroNotationVersion.cs:76-98` |
| INV-3.6 | 🟢 ⚠️ Długość liczona na **surowym** wejściu | `>12` znaków odrzucone niezależnie od zwijania zer — forum emituje krótkie 2-3-segmentowe wersje. | `LotroNotationVersion.cs:42-46` |

---

## 4. Translator — tożsamość i prowizjonowanie

Lokalna projekcja uwierzytelnionego użytkownika (ADR-0004). `IdentityId` to jedyna cross-contextowa
referencja do AuthSystem.

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-4.1 | 🟢 `IdentityId` niezmienny, wymagany | `get`-only; `Create` wymaga `Ensure.NotEmpty(identityId)`. | `Translator.cs:21`, `:45` |
| INV-4.2 | 🟢 `RefreshProfile` zbiega profil z bieżących claimów | Odświeża `DisplayName`/`Email` (bez znacznika czasu); `null` email **czyści** wcześniejszy. Przemianowane konto zbiega bez osobnej synchronizacji. | `Translator.cs:31-37` |
| INV-4.3 | 🔵 `Translators.IdentityId` **unikalny** (klucz idempotencji) | Unikalny indeks DB gwarantuje jeden wiersz na tożsamość nawet w wyścigu. | `TranslatorConfiguration.cs:20-21` |
| INV-4.4 | 🔵 Prowizjonowanie **leniwe i idempotentne** przy pierwszym uwierzytelnionym **żądaniu** (ADR-0004 aneks 2026-06-24) | Middleware `TranslatorProvisioningMiddleware` woła `ProvisionCurrentAsync` best-effort na każdym uwierzytelnionym żądaniu (failure logowany, nie blokuje odczytu); handlery zapisu prowizjonują autorytatywnie. Get-or-create po `IdentityId`; przy `DbUpdateException` (wyścig „pierwszego dotknięcia") odpina swój insert i re-czyta zacommitowany wiersz. Rozwiązanie tożsamość→`TranslatorId` cache'owane w L1 `HybridCache` (TTL 5 min, fingerprint claimów; failure nigdy nie jest cache'owany — PERF-07). | `TranslatorProvisioningMiddleware.cs:32-44`, `TranslatorProvisioner.cs:167-231` |
| INV-4.5 | 🔵 Brak tożsamości w tokenie → `Forbidden` | Gdy `MaybeIdentityId.HasNoValue`. | `Translators.Unauthenticated` (`TranslatorProvisioner.cs:66-75`) |
| INV-4.6 | 🔵 ⚠️ `DisplayName` = claim `name`, fallback `email` | Token zawsze niesie co najmniej jeden; brak obu → błąd walidacji VO. Email malformowany → po prostu brak emaila (nie wywala zapisu). | `TranslatorProvisioner.cs:81-89`, `:236-249` |

| Value Object | Reguła | Dokładna wartość | Błąd |
|--------------|--------|------------------|------|
| **DisplayName** | niepuste, max długość (trim) | **150** | `TranslatorEntity.DisplayName.NullOrEmpty` (`:19`) / `.LongerThanAllowed` (`:26`) |
| **Email** (opcjonalny) | niepuste, max długość, regex | **250**, `^[^@\s]+@[^@\s]+\.[^@\s]+$` | `TranslatorEntity.Email.InvalidFormat` (`Email.cs:27`,`:39`) / `.LongerThanAllowed` (`:34`) |

---

## 5. PrecomputedTranslationFile — projekcja dystrybucyjna

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-5.1 | 🟢 ⚠️ To `Entity`, **nie** `AggregateRoot` | Nie broni żadnego invariantu; wyprowadzony, regenerowalny; typ niemutowalny. Persystowany przez `IPrecomputedTranslationFileStore` (Store ≠ Repository). (ADR-0007) | `PrecomputedTranslationFile.cs:19` |
| INV-5.2 | 🟢 Jeden wiersz na język (klucz naturalny) | Store upsertuje po `Language`: `TryRefreshAsync` = set-based `UPDATE` w miejscu (nigdy nie materializuje poprzedniego multi-MB contentu — PERF-04); `Insert` tylko dla pierwszego wiersza języka. | `PrecomputedTranslationFile.cs:29`, `IPrecomputedTranslationFileStore.cs:16-23` |
| INV-5.3 | 🔵 Plik = **Approved + nieunieważnione + nieusunięte**, sort `FileId` → `GossipId` | Regenerowany po każdym zapisie zmieniającym zbiór dystrybucji (import / upsert na Approved / approve / bulk approve) — **w tle, z debounce** (ADR-0021): handler sygnalizuje `ITranslationFileRebuildScheduler` po commicie, worker koalescuje sygnały (okno domyślnie 2 s, max 5 min) i odpala jedną single-flight przebudowę; pobranie może chwilowo „gonić" commit. | `PrecomputedTranslationFileProjector.cs:32-46`, `TranslationFileRebuildScheduler.cs`, `TranslationFileRebuildSettings.cs` |
| INV-5.4 | 🔵 Kolumna `approved` zawsze `1`, terminatory CRLF | Serializacja byte-compatible z writerem patchera (golden fixture + round-trip). ETag = hex SHA-256 contentu i pełni też rolę hasha integralności po stronie patchera (AUDIT-SEC-01) — zmiana algorytmu/formatu tylko razem z patcherem. | `TranslationFileSerializer.cs:14-38` |

---

## 6. Serwis domenowy — TranslationDiffService

Czysty diff (`static class`, `ComputePlanAsync`) uploadu względem zapisanego stanu, kluczowany po
tożsamości fragmentu — od spec 0006 strumieniowo i na wierszach wartości (mapa klucz→`SourceHash` vs
strumień `StoredSourceDigest`; porównanie hash-do-hasha, bez stringów źródeł i bez agregatów).
**Nie dotyka bazy** — handler realizuje plan po przejściu guarda truncacji.

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-6.1 | 🟢 Pięć rozłącznych wyników na wiersz | **Added** (para nieznana) · **Source-changed** (hash różny) · **Removed** (zapisana para nieobecna) · **Restored** (usunięta para wraca z identycznym źródłem) · **Unchanged**. | `TranslationDiffService.cs:37-73` |
| INV-6.2 | 🟢 Unieważnienie liczy się tylko gdy wiersz **miał polski** | `HasPolish` = `Draft`/`Approved`/`NeedsReview`. | `TranslationDiffService.cs:85-86` |
| INV-6.3 | 🟢 `RemovedFraction` = 0 dla baseline | Brak aktywnych wierszy ⇒ mianownik 0 ⇒ ułamek 0; guard truncacji nigdy nie tripuje na pierwszym wczytaniu. | `TranslationDiffPlan.cs:44-45` |
| INV-6.4 | 🟢 `Removed` liczy tylko aktywne, nieobecne pary | `!stored.IsRemoved` i klucz nieobecny w uploadzie. | `TranslationDiffService.cs:44-52` |

---

## 7. Aplikacja — import

`POST /api/v1/game-versions/{id}/import` (admin). Walidator komendy + handler; dwa passy
strumieniowe (spec 0006): pass 1 waliduje i diffuje bez żadnego zapisu, pass 2 realizuje plan w
**jednej atomowej transakcji** (COPY dla added, chunkowane mutacje dla reszty — ADR-0011;
all-or-nothing, idempotentny re-upload). Błędy importu = `DataConflict` → **422**.

| # | Invariant | Reguła | Błąd |
|---|-----------|--------|------|
| INV-7.1 | 🔵 `GameVersionId` niepusty, `FileStream` nienull (i seekable) | Walidator komendy; endpoint gwarantuje seekowalność (bufor multipart / kopia do pliku tymczasowego). | `ImportExportedTexts.cs:50-61`, `:491-521` |
| INV-7.2 | 🔵 Nieznana wersja → `NotFound` (404) | Repo lookup przed parsowaniem. | `GameVersionEntity.NotFound` (`ImportExportedTexts.cs:115-119`) |
| INV-7.3 | 🔵 Plik z błędami parsowania odrzucony | Truncowany plik nie może udawać masowego usunięcia; zbierane max 100 błędów linii do komunikatu. | `Import.ParseFailed` (`ImportExportedTexts.cs:215-263`, `ImportErrors.cs:14`) |
| INV-7.4 | 🔵 Pusty upload odrzucony | Plik bez wierszy nie oznaczy wersji jako przetworzonej. | `Import.EmptyUpload` (`ImportExportedTexts.cs:135-138`, `ImportErrors.cs:30`) |
| INV-7.5 | 🔵 Niepoprawny wiersz / zduplikowany klucz odrzuca cały import | Walidacja VO per wiersz + guard unikalności klucza przy budowie mapy klucz→hash. | `Import.InvalidRow` / `Import.DuplicateFragmentKey` (`ImportExportedTexts.cs:236-256`) |
| INV-7.6 | 🔵 ⚠️ Masowe usunięcie > **20%** aktywnych blokowane bez override | `RemovedFraction > MaxRemovedFractionWithoutOverride` (domyślnie `0.20`) i bez `allowMassRemoval=true` → fail — na pełnym planie, przed jakimkolwiek zapisem. | `Import.MassRemovalBlocked` (`ImportExportedTexts.cs:154-162`, `ImportSettings.cs:15`) |
| INV-7.7 | 🔵 Flaga `Processed` flipuje w **tej samej** transakcji co diff | Przejście pre-checkowane przed zapisem (superseded → 422 bez persystencji), a persystowany flip commit-uje z ostatnim save'em transakcji apply; potem **zaplanowany** (debounced, ADR-0021) rebuild artefaktu. | `ImportExportedTexts.cs:164-201`, `:302-314` |
| INV-7.8 | 🔵 Przetworzenie najnowszej wersji **superseduje** starsze `Unprocessed` | W tej samej transakcji apply każda wcześniej wykryta, wciąż nieprzetworzona wersja dostaje `MarkSuperseded` — uzbraja guard stale-exportu (INV-3.2); raportowane w `warnings`. | `ImportExportedTexts.cs:316-335` |
| INV-7.9 | 🔵 Upload ograniczony rozmiarem | `RequestSizeLimit` = `Import:MaxUploadBytes` (domyślnie **256 MB**, `ImportUploadLimits.MaxUploadBytes`) → ponad limit **413**. | `ImportExportedTexts.cs:477-531`, `ImportSettings.cs:23`, `ImportUploadLimits.cs:13` |

---

## 8. Aplikacja — translations (upsert / approve / odczyty)

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-8.1 | 🔵 Upsert: `FileId > 0`, `GossipId >= 0`, `TranslatedText` niepuste | Walidator komendy. | `UpsertTranslation.cs:41-54` |
| INV-8.2 | 🔵 Upsert nieistniejącego fragmentu → `NotFound` (404) | Wiersz rodzi się z importu, nie z upsertu. | `TranslationEntity.NotFound(fileId,gossipId)` (`UpsertTranslation.cs:99-103`) |
| INV-8.3 | 🔵 Upsert na miękko usuniętym → 422 | Usunięty wiersz wykluczony z pracy. | `TranslationEntity.CannotEditRemoved` (`UpsertTranslation.cs:107-112`) |
| INV-8.4 | 🔵 Upsert prowizjonuje submittera **przed** stemplowaniem FK | Autorytatywne prowizjonowanie w handlerze (middleware z INV-4.4 jest tylko best-effort). | `UpsertTranslation.cs:114-120` |
| INV-8.5 | 🔵 Edycja `Approved` planuje rebuild artefaktu | `wasApproved` ⇒ `Schedule` po commicie (wiersz wypadł ze zbioru dystrybucji); rebuild debounced w tle (ADR-0021). | `UpsertTranslation.cs:122-136` |
| INV-8.6 | 🔵 Approve: `Id ≠ Empty` (walidator); nieznany → 404; prowizjonuje approvera; rebuild zaplanowany po sukcesie | Po `Approve` wiersz (re)wchodzi do zbioru dystrybucji ⇒ artefakt przebudowany w tle (debounced). | `ApproveTranslation.cs:33-41`, `:77-104` |
| INV-8.7 | 🔵 Lista: nieobsługiwany `lang` → 400 (walidacja inline); lista jest **anonimowa** (read-only, #309) | Zapytania walidują się w handlerze; dziś tylko `polish`. Anonimowy caller dostaje itemy bez żadnych linków HATEOAS. | `Translations.UnsupportedLanguage` (`ListTranslations.cs:65-73`, `:193`) |
| INV-8.8 | 🔵 Lista **wyklucza** miękko usunięte; domyślny sort `(FileId, GossipId)` + opcjonalny `?sort=` po białej liście; `pageSize` clamp 1–100 | `RemovedInVersion == null`; `Page`≥1, `PageSize` clamp; `sort` (`fileId`/`gossipId`/`status`/`submittedAt`, `:asc`/`:desc`) zawsze z doklejonym unikalnym tiebreakerem — porządek totalny; nieznany klucz degraduje do defaultu. | `ListTranslations.cs:48-49`, `:75-76`, `:96-135` |
| INV-8.9 | 🔵 ⚠️ Search escapuje metaznaki LIKE | Dosłowne `%`/`_` ze źródła LOTRO matchują literalnie (ILIKE, escape `\`). | `ListTranslations.cs:83-92`, `:137-144` |
| INV-8.10 | 🔵 Get-one: id `Empty` → `NotFound` (short-circuit przed DB) | Same zera nigdy nie identyfikują wiersza. | `GetTranslation.cs:43-46` |
| INV-8.11 | 🔵 Stats liczy aktywny katalog; `Translated` = Draft+Approved+NeedsReview | Jedno grupowane zapytanie; `Remaining = Total - Approved`; snapshot z cache serwerowego **30 s** (`HybridCache`, AUDIT-EF-04). | `GetTranslationStats.cs:41-45`, `:74-104` |
| INV-8.12 | 🔵 Bulk approve (#322): 1–100 **różnych** id (walidator + dedup); best-effort | Tylko wiersze wciąż `Draft`/`NeedsReview` są zatwierdzane (guard domenowy `Approve` autorytatywny — np. równolegle usunięty wiersz jest pomijany); reszta liczona jako skipped, **bez** per-row 404/422; `Approved + Skipped == Requested`. Jeden `SaveChanges` i jeden zaplanowany rebuild tylko gdy ≥ 1 zatwierdzony. Prowizjonowanie approvera przed batchem — failure wywala całość. | `BulkApproveTranslations.cs:44-60`, `:96-146` |
| INV-8.13 | 🔵 Publiczny snapshot postępu (`GET /progress`, #309) | Anonimowy; te same kubełki co stats + najnowsza **Processed** wersja gry (`null` przed pierwszym importem); tylko liczniki agregatowe; cache serwerowy 30 s. | `GetPublicProgress.cs:80-111`, `:114-129` |

---

## 9. Aplikacja — game versions

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-9.1 | 🔵 Register: `Version` niepuste, ≤ **12** znaków | Walidator komendy. | `RegisterGameVersion.cs:28-36` |
| INV-9.2 | 🔵 Duplikat wersji → 422 (po formie kanonicznej) | `ExistsByVersionAsync(version)` po `LotroNotationVersion.Create` (a więc po kanonikalizacji). | `GameVersionEntity.LotroNotationVersion.AlreadyTaken` (`RegisterGameVersion.cs:74-77`) |
| INV-9.3 | 🔵 Register zwraca **201** z `Location` na item-endpoint | `/api/v1/game-versions/{id}`. | `RegisterGameVersion.cs:108-111` |
| INV-9.4 | 🔵 Lista niestronicowana, domyślny sort `DetectedAt` malejąco + opcjonalny `?sort=` | Niewiele wierszy (jeden na update gry); klucze `version`/`detectedAt`/`status` (sort po **stringu**, nie semantycznie); nieznany klucz degraduje do `DetectedAt` **rosnąco**. | `ListGameVersions.cs:42-47`, `:67-74` |
| INV-9.5 | 🔵 Get-one: id `Empty` → `NotFound` | Short-circuit przed DB. | `GetGameVersion.cs:38-42` |
| INV-9.6 | 🔵 Delete (#209, admin): tylko `Unprocessed` **i** niereferowana | Guard domenowy `EnsureCanBeDeleted` (INV-3.7) + cross-agregatowy check `AnyReferencesGameVersionAsync` w handlerze → 422; nieznany id → 404; sukces → **204**. Link HATEOAS `delete` tylko dla admina na wersji `Unprocessed`. | `GameVersionEntity.CannotDeleteReferencedVersion` (`DeleteGameVersion.cs:76-93`) |

---

## 10. Aplikacja — dystrybucja pliku

`GET /api/v1/translation-files/{lang}` — **anonimowy** (CLI/player pobiera).

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-10.1 | 🔵 `lang` musi być `polish` | Inaczej walidacja inline. | `TranslationFiles.UnsupportedLanguage` (400) (`GetTranslationFile.cs:151-157`) |
| INV-10.2 | 🔵 Brak zbudowanego artefaktu → 404 | Plik serwowany ze store, nigdy budowany per-request (rebuild w tle — INV-5.3). | `TranslationFiles.NotFound` (`GetTranslationFile.cs:159-163`) |
| INV-10.3 | 🔵 ETag = content hash; `If-None-Match` → **304** | Strong tag; `*` lub dopasowanie ⇒ 304 — decyzja z hash-only lookupu, bez czytania multi-MB contentu (PERF-01/#286). `Cache-Control: private, no-cache`. | `GetTranslationFile.cs:100-131`, `:37-62` |
| INV-10.4 | 🔵 ⚠️ ETag = hash integralności (AUDIT-SEC-01/#391) | Hex SHA-256 body UTF-8; patcher re-liczy hash pobranego pliku i odrzuca przy niezgodności — algorytm + format strong-ETag to kontrakt cross-contextowy (zmiana tylko razem z patcherem). | `GetTranslationFile.cs:23-26` |

---

## 11. AuthSystem — uwierzytelnianie

Lifted wholesale (OpenIddict + ASP.NET Identity). Pełna narracja: [auth-tutorial.md](auth-tutorial.md).

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-11.1 | 🔵 Hasło: **8–128**, ≥1 cyfra/mała/wielka/specjalny | FluentValidation; Identity `RequiredLength=8` (⚠️ bez górnego limitu na poziomie Identity). | `PasswordValidationRules.cs:13-22`, `PersistenceDependencyInjection.cs:47` |
| INV-11.2 | 🔵 **E-mail jest loginem** (ADR-0022): unikalny **case-insensitive** ≤ 250 regex; Username = **handle display-only**: unikalny (case-insensitive), `^[a-zA-Z0-9]+$` ≤ 150 | Walidator rejestracji + `UsernameConstants` + Identity `AllowedUserNameCharacters`; unikalność e-maila fizyczna przez **unikalny `EmailIndex`** na `NormalizedEmail`. | `RegisterUser.cs`, `UsernameConstants.cs`, `PersistenceDependencyInjection.cs`, `ApplicationUserConfiguration.cs` |
| INV-11.3 | 🔵 Zgody privacy + data-processing + **terms-of-service** (LEGAL-03) **muszą być true** | Inaczej walidacja rejestracji odrzuca; akceptacja ToS + jej timestamp są persystowane i widoczne w data-exporcie. | `RegisterUser.cs:49-57`, `:116-117` |
| INV-11.4 | 🔵 ⚠️ Nowy użytkownik dostaje rolę **`Translator`** | Self-register → `Translator`; seedowany admin → `Admin`. | `RegisterUser.cs:138-139`, `DatabaseSeederExtensions.cs:103` |
| INV-11.5 | 🔵 Email confirmation **wymagane do logowania**; lockout **5 prób / 5 min** | `SignIn.RequireConfirmedEmail`, `Lockout.MaxFailedAccessAttempts=5`. | `PersistenceDependencyInjection.cs:50-52` |
| INV-11.6 | 🔵 Tokeny: access **60 min**, refresh **14 dni** (referencyjne, rolling); email/reset **24 h** | Refresh tokeny w bazie ⇒ rewokowalne; rotacja przy użyciu. | `OpenIddictSettings.cs:16-17`, `OpenIddictExtensions.cs:50`, `PersistenceDependencyInjection.cs:63` |
| INV-11.7 | 🔵 Anti-enumeration | Forgot/Resend zawsze sukces; token endpoint i strona logowania dummy-hash przy braku usera (i lockout); gate potwierdzenia e-maila **po** weryfikacji hasła. | `TokenEndpoint.cs:20-21`, `Login.cshtml.cs` |
| INV-11.8 | 🔵 ⚠️ **Brak sagi rejestracji** | Rejestracja tworzy tylko usera Auth; profil `Translator` powstaje leniwie na pierwszym uwierzytelnionym żądaniu TMS (ADR-0002 §7 / ADR-0004 z aneksem). | `RegisterUser.cs:166-172` |
| INV-11.9 | 🔵 `DeleteAccount` **planuje** kasowanie RODO — **14-dniowe okno anulowania** (ADR-0031 / LEGAL-01) | Wymaga hasła; nic nie jest kasowane od razu: `DeletionScheduledAt` = teraz, `LockoutEnd` = koniec okna, rotacja security stampa + rewokacja tokenów OpenIddict, e-mail z jednorazowym linkiem anulowania (brak maila ⇒ schedule jest cofany). Erasure wykonuje finalizer **po** upływie okna; anulowanie zdejmuje lockout i **wymusza reset hasła**; reset hasła w trakcie okna jest zablokowany (tylko link z maila przywraca konto). | `DeleteAccount.cs:101-118`, `:138-152`, `CancelAccountDeletion.cs`, `AccountDeletionFinalizer.cs:41-53`, `ResetPassword.cs:86-93`, `GdprSettings.cs:7` |
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
- **⚠️ Brak parsowania semver (INV-3.4).** Porządek wersji to `DetectedAt` (chronologia rejestracji —
  dziś ręcznej, watcher forum #85 niezaimplementowany), nie porównanie numeryczne — DAT vnum jest
  bezużyteczny jako wersja treści (knowledge base).
- **⚠️ Masowe usunięcie 20% (INV-7.6).** Próg domyślny, override przez `allowMassRemoval=true` —
  truncowany/częściowy export nie może udawać masowego usunięcia (spec 0001 Q4).
- **⚠️ Nowy user = `Translator`, nie `User` (INV-11.4).** Role to `Admin`/`Translator` (nie
  KittySaverowe `Admin`/`User`/`Moderator`).
- **⚠️ Brak sagi rejestracji (INV-11.8).** Świadomy non-lift — `Translator` prowizjonowany leniwie.
- **Jeden enum zamiast bool+flaga.** `TranslationStatus` i `GameVersionStatus` kodują pełny cykl życia
  w jednym enumie — nielegalne kombinacje niereprezentowalne (spec 0001 Q6).
- **Projekcja ≠ agregat (INV-5.1).** `PrecomputedTranslationFile` to `Entity` za `Store`, nie agregat
  za `Repository` — celowo (ADR-0007).
- **⚠️ Rebuild artefaktu jest asynchroniczny (INV-5.3, ADR-0021).** Zapis tylko sygnalizuje; worker
  koalescuje (debounce, domyślnie 2 s) i przebudowuje w tle — pobranie pliku może chwilowo „gonić"
  commit. Liczniki stats/progress mają dodatkowo cache serwerowy 30 s (INV-8.11, INV-8.13).
- **⚠️ Publiczne odczyty (#309).** Lista tłumaczeń i `GET /progress` są anonimowe (dane = publiczne
  teksty gry); każda tranzycja stanu pozostaje uwierzytelniona, a anonimowy caller nie dostaje
  linków HATEOAS.
- **⚠️ Bulk approve jest best-effort (INV-8.12).** Nieaprobowalne wiersze są pomijane i liczone jako
  skipped — pojedynczy przeterminowany wiersz nigdy nie wywala batcha (kontrast z per-row 404/422
  pojedynczego approve).
