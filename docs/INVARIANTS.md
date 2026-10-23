# Invarianty biznesowe — LotroKoniecDev TMS

> **Katalog reguł, które TMS egzekwuje w kodzie** (warstwa Domain, serwis domenowy, walidatory
> i handlery aplikacji, konfiguracja Auth). Kod jest źródłem prawdy dla tego dokumentu, a wiki dla
> reguł biznesowych (`CLAUDE.md` → „Source of truth").
>
> Zakres: kontekst **TMS** (`src/TranslationSystem/**` + `src/AuthSystem/**`). Patcher
> (`src/Patcher/**`) ma własny model i nie jest tu opisany. Lokalizacja to plik; numery linii
> szybko się dezaktualizują, więc ich nie podajemy.

## Jak czytać ten dokument

- **Invariant** = reguła, która zawsze musi być prawdziwa w danym punkcie cyklu życia.
- Gdzie reguła jest egzekwowana:
  - 🟢 **Domena** — encja, value object, serwis domenowy (twardy invariant, nie do obejścia).
  - 🔵 **Aplikacja** — walidator FluentValidation, handler, konfiguracja (reguła wejścia, poza agregatem).
- **Guard vs Result:** `Ensure.*` / `ArgumentNullException` / `ArgumentException` **rzucają wyjątek**
  (błąd programisty). Reszta zwraca `Result.Failure(Error)` (błąd dla użytkownika, mapowany na
  `ProblemDetails` — [API.md §5](API.md#5-error-contract-problemdetails)).
- ⚠️ = miejsce, w którym kod może zaskoczyć — zebrane w [sekcji 12](#12-rozbieżności-i-rzeczy-warte-uwagi).
- **Walidatory są tylko dla komend** (house rule); zapytania walidują się w handlerze.

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
| INV-0.1 | 🟢 Błędy biznesowe są **wartościami**, nie wyjątkami | Reguła, która może zawieść dla użytkownika, zwraca `Result.Failure(Error)`/`Maybe.None`. `Ensure.*` / `ArgumentNullException` / `ArgumentException` rzucają — tylko na błędy programisty. | całe Domain |
| INV-0.2 | 🟢 Brak prymitywnej obsesji | Koncept z ograniczeniem lub tożsamością to `ValueObject` albo strongly-typed ID (`FragmentKey`, `TranslationSource`, `LotroNotationVersion`, `DisplayName`, `Email`, `*Id`). | `Domain/Aggregates/**/ValueObjects` |
| INV-0.3 | 🟢 Walidacja string-VO: **pustość → trim → długość → format** | Najpierw `IsNullOrWhiteSpace` na surowej wartości, potem `Trim()`, potem długość, potem regex lub gramatyka (`LotroNotationVersion` sprawdza jeszcze liczbę członów przed długością). Skutek: same spacje dają `NullOrEmpty`/`InvalidFormat`, nigdy `LongerThanAllowed`. | `LotroNotationVersion.cs`, `DisplayName.cs`, `Email.cs` |
| INV-0.4 | 🟢 Strongly-typed ID to **GUID v7** | Wszystkie `*Id` (`TranslationId`, `GameVersionId`, `TranslatorId`, `PrecomputedTranslationFileId`, `IdentityId`) powstają przez `Guid.CreateVersion7()` (uporządkowane w czasie) i w JSON są gołym stringiem GUID (ADR-0010). | `Primitives/**`, `SharedKernel/StronglyTypedIds` |
| INV-0.5 | 🟢 Zmiany encji ze znacznikiem czasu stemplują `UpdatedAt`/`GeneratedAt` | Metody zachowań ustawiają `now` z `TimeProvider` (projekcja: `TryRefreshAsync` w store stempluje `GeneratedAt`). `Translator` nie ma znacznika zmian — tylko niezmienny `ProvisionedAt`. | `Translation.cs`, `IPrecomputedTranslationFileStore.cs` |
| INV-0.6 | 🔵 Endpointy są **autoryzowane domyślnie** | Fallback policy wymaga zalogowanego użytkownika. Publiczne endpointy mają jawne `AllowAnonymous`: dokument usług `GET /`, pobranie pliku, `GET /progress` i lista tłumaczeń tylko do odczytu (#309). Autor zmiany jest brany z tokena, nigdy z body. | `ApiDependencyInjection.cs` (`AddAuthorizationPolicies`) |

---

## 1. Translation — maszyna stanów i cykl życia

**Statusy (`TranslationStatus`):** `Untranslated`, `Draft`, `Approved`, `NeedsReview` (+ `Unset`,
nigdy nieustawiany). Wiersz powstaje jako **`Untranslated`**. **Jeden enum, bez osobnej flagi
unieważnienia**, więc nielegalnej kombinacji (np. „Approved i unieważniony") nie da się zapisać
(spec 0001 Q6). Unieważnienie *jest* przejściem do `NeedsReview`.

### Dozwolone przejścia

| Z | Do | Metoda | Warunki / efekt |
|---|----|--------|-----------------|
| (nowy) | `Untranslated` | `CreateUntranslated` | samo angielskie źródło; stempluje `IntroducedInVersion` |
| dowolny | `Draft` | `ProvideTranslation` | zapisuje polski, stempluje autora |
| `Draft`/`NeedsReview` | `Approved` | `Approve` | wymaga polskiego i nieusuniętego wiersza; czyści `PreviousSourceText` |
| `Draft`/`Approved` | `NeedsReview` | `ApplySourceChange` | unieważnienie: zamraża `PreviousSourceText` |

| # | Invariant | Reguła | Błąd / lokalizacja |
|---|-----------|--------|--------------|
| INV-1.1 | 🟢 `FragmentKey` (tożsamość) jest **niezmienny** | Tylko `get`, ustawiany w konstruktorze; taki sam we wszystkich wersjach gry. | `Translation.cs` |
| INV-1.2 | 🟢 Wiersz rodzi się jako `Untranslated` | Konstruktor domenowy ustawia `Status = Untranslated`. | `Translation.cs` |
| INV-1.3 | 🟢 `ApplySourceChange` unieważnia polski **tylko** z `Draft`/`Approved` | Wtedy `PreviousSourceText = Source.Text` i `Status → NeedsReview`. Zawsze nadpisuje `Source`, czyści miękkie usunięcie i stempluje `LastSourceChangeInVersion`. | `Translation.cs` |
| INV-1.4 | 🟢 ⚠️ `PreviousSourceText` **zamrożony** — kolejne zmiany źródła na `NeedsReview` go nie nadpisują | Tłumacz dalej widzi angielski, pod który napisano *obecny* polski. | `Translation.cs` |
| INV-1.5 | 🟢 `MarkRemoved` to **miękkie** usunięcie | Ustawia `RemovedInVersion`; wiersz wypada z pracy i z pliku, nigdy nie jest kasowany. `IsRemoved => RemovedInVersion is not null`. | `Translation.cs` |
| INV-1.6 | 🟢 `Restore` zachowuje poprzedni status | Czyści tylko `RemovedInVersion`; status (także `Approved`) zostaje, bo stary polski nadal pasuje. | `Translation.cs` |
| INV-1.7 | 🟢 `ProvideTranslation`: **każdy** poprzedni status → `Draft` | Edycja `Approved` wyjmuje wiersz z dystrybucji do ponownej akceptacji; `PreviousSourceText` zostaje do approve. Pusty tekst albo dłuższy niż `DatFormatConstants.MaxTranslatedTextLength` (32767) rzuca wyjątek — to guard programisty za walidatorem (ADR-0043). | `Translation.cs` |
| INV-1.8 | 🟢 Nie zatwierdzisz wiersza **bez polskiej treści** | `Approve` na pustym `TranslatedText` → błąd. | `TranslationEntity.CannotApproveWithoutTranslation` |
| INV-1.9 | 🟢 Nie zatwierdzisz wiersza **miękko usuniętego** | `Approve` na `IsRemoved` → błąd. | `TranslationEntity.CannotApproveRemoved` |
| INV-1.10 | 🟢 `Approve` czyści unieważnienie | → `Approved`, stempluje recenzenta, `PreviousSourceText = null` — wiersz wraca do zbioru dystrybucji. | `Translation.cs` |

---

## 2. Translation — Value Objects

| Value Object | Reguła | Dokładna wartość | Błąd |
|--------------|--------|------------------|------|
| **FragmentKey** | `FileId` dodatni, `GossipId` nieujemny; tożsamość `(FileId, GossipId)` | `FileId > 0`, `GossipId >= 0` | `TranslationEntity.FileId.Invalid` / `TranslationEntity.GossipId.Invalid` (`FragmentKey.cs`) |
| **TranslationSource** | `Text` + `ArgsOrder` + `ArgsId` jako **jedna** wartość; `Text` nie może być `null` (guard) | pusta kolumna args i `NULL` → `null` | `ArgumentNullException` na `text` (programista) |

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-2.1 | 🟢 ⚠️ `GossipId == 0` jest **legalny** | Parser patchera nie ma dolnej granicy; tylko liczba ujemna to uszkodzone dane. Jeden prawdziwy wiersz nie może wywalić całego importu. | `FragmentKey.cs` |
| INV-2.2 | 🟢 ⚠️ Zmiana struktury argumentów = zmiana znaczenia | Równość `TranslationSource` obejmuje `Text` **oraz** obie kolumny args, więc diff traktuje samą zmianę placeholderów jako zmianę źródła (spec 0001). | `TranslationSource.cs` |
| INV-2.3 | 🟢 `NULL`/pusta kolumna args → `null` | `NormalizeArgs`: `IsNullOrWhiteSpace` lub `NULL` (bez względu na wielkość liter) → `null`, żeby „brak" i „NULL" nie liczyły się jako zmiana. | `TranslationSource.cs` |
| INV-2.4 | 🟢 Puste źródło jest legalne i przechodzi round trip | Pusty fragment to poprawna treść gry; `text` nie może być tylko `null` (guard). | `TranslationSource.cs` |

---

## 3. GameVersion — maszyna stanów i wartość

**Statusy (`GameVersionStatus`):** `Unprocessed`, `Processed`, `Superseded` (+ `Unset`, nigdy
nieustawiany). Wersja powstaje jako **`Unprocessed`**.

| # | Invariant | Reguła | Błąd |
|---|-----------|--------|------|
| INV-3.1 | 🟢 Wersja rodzi się jako `Unprocessed` | Konstruktor ustawia `Status = Unprocessed`. | `GameVersion.cs` |
| INV-3.2 | 🟢 `Superseded` **nigdy** nie zostanie przetworzony | `MarkAsProcessed` na `Superseded` → błąd; każdy inny status → `Processed` (ponowny import do `Processed` jest dozwolony i nic nie zmienia). | `GameVersionEntity.SupersededCannotBeProcessed` |
| INV-3.3 | 🟢 `Processed` **nie wraca** do `Superseded` | `MarkSuperseded` na `Processed` → błąd (zrobionej pracy nie cofamy). Starsze `Unprocessed` są oznaczane hurtem, gdy nowsza wersja zostaje przetworzona. | `GameVersionEntity.ProcessedCannotBeSuperseded` |
| INV-3.4 | 🟢 `LotroNotationVersion`/`DetectedAt` niezmienne | Tylko `get`, ustawiane w konstruktorze. ⚠️ `DetectedAt` = porządek wersji (kolejność rejestracji; **bez porównywania numerów**). | `GameVersion.cs` |
| INV-3.7 | 🟢 `Processed` nie może być **usunięta**; `Unprocessed`/`Superseded` mogą | `EnsureCanBeDeleted` na `Processed` → błąd: tylko do tej wersji był import, więc wskazują na nią tłumaczenia (spec 0001). Pozostałe nie miały importu, więc ich usunięcie zwalnia numer wersji (#624). Warunek między agregatami sprawdza handler (INV-9.6). | `GameVersionEntity.ProcessedCannotBeDeleted` |

| Value Object | Reguła | Dokładna wartość | Błąd |
|--------------|--------|------------------|------|
| **LotroNotationVersion** | niepuste, liczba członów, długość, gramatyka `digits(.digits)*`, **forma kanoniczna** | od 1 do **3** członów; max **12** znaków (surowych) | `GameVersionEntity.LotroNotationVersion.NullOrEmpty` / `.MoreSegmentsThanAllowed` / `.LongerThanAllowed` / `.InvalidFormat` (`LotroNotationVersion.cs`) |

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-3.5 | 🟢 ⚠️ Forma kanoniczna — `48` = `48.0` = `48.0.0` | Końcowe zerowe człony są obcinane, wiodące zera w członie usuwane (`047` → `47`). Zera w środku (`47.0.1`) zostają. Takie VO są równe (ADR-0003). | `LotroNotationVersion.cs` |
| INV-3.6 | 🟢 ⚠️ Długość liczona na **surowym** wejściu | `>12` znaków odrzucone niezależnie od obcinania zer — forum publikuje krótkie, 2-3-członowe wersje. | `LotroNotationVersion.cs` |
| INV-3.8 | 🟢 ⚠️ Najwyżej **trzy człony**, liczone na surowym wejściu | Czwarty człon → `MoreSegmentsThanAllowed`, także gdy po obcięciu zer zostałoby mniej (`47.1.0.0` odrzucone). Reguła z wiki (ADR-0003, aneks 2026-08-28). | `LotroNotationVersion.cs` |

---

## 4. Translator — tożsamość i prowizjonowanie

Lokalny profil zalogowanego użytkownika (ADR-0004). `IdentityId` to jedyna referencja do AuthSystem
między kontekstami.

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-4.1 | 🟢 `IdentityId` niezmienny, wymagany | Tylko `get`; `Create` wymaga `Ensure.NotEmpty(identityId)`. | `Translator.cs` |
| INV-4.2 | 🟢 `RefreshProfile` kopiuje bieżące claimy | Odświeża `DisplayName`/`Email` (bez znacznika czasu); `null` email **czyści** wcześniejszy. Provisioner woła ją tylko, gdy claimy się zmieniły. Przemianowane konto nadąża samo. | `Translator.cs`, `TranslatorProvisioner.cs` |
| INV-4.3 | 🔵 `Translators.IdentityId` **unikalny** (klucz idempotencji) | Unikalny indeks w bazie daje jeden wiersz na tożsamość także przy wyścigu. | `TranslatorConfiguration.cs` |
| INV-4.4 | 🔵 Prowizjonowanie **leniwe i idempotentne** przy pierwszym uwierzytelnionym **żądaniu** (ADR-0004, aneks 2026-06-24) | `TranslatorProvisioningMiddleware` woła `ProvisionCurrentAsync` na każdym uwierzytelnionym żądaniu; błąd jest logowany i nie blokuje odczytu. Handlery zapisu prowizjonują same i zwracają błąd. Get-or-create po `IdentityId`; przy `DbUpdateException` (wyścig dwóch pierwszych żądań) odpina swój insert i czyta zapisany wiersz. Wynik tożsamość → `TranslatorId` jest w `HybridCache` (TTL 5 min, odcisk claimów; błąd nigdy nie trafia do cache — PERF-07). | `TranslatorProvisioningMiddleware.cs`, `TranslatorProvisioner.cs` |
| INV-4.5 | 🔵 Brak tożsamości w tokenie → `Forbidden` | Gdy `MaybeIdentityId.HasNoValue`. | `Translators.Unauthenticated` (`TranslatorProvisioner.cs`) |
| INV-4.6 | 🔵 ⚠️ `DisplayName` = claim `name`, a gdy go brak — `email` | Token zawsze niesie co najmniej jeden; brak obu → błąd walidacji VO. Błędny email daje po prostu brak emaila (zapis się udaje). | `TranslatorProvisioner.cs` |
| INV-4.7 | 🔵 Usunięcie konta w AuthSystem zdejmuje osobę z profilu (ADR-0065) | Konsument wiadomości `account.erased` woła `Erase()`: `DisplayName` = `Usunięte konto`, `Email` = `null`. Profil i jego tłumaczenia zostają; powtórka nic nie zmienia. | `Translator.cs`, `AccountErasedConsumer.cs`, `EraseTranslatorProfile.cs` |

| Value Object | Reguła | Dokładna wartość | Błąd |
|--------------|--------|------------------|------|
| **DisplayName** | niepuste, max długość (po trim) | **150** | `TranslatorEntity.DisplayName.NullOrEmpty` / `.LongerThanAllowed` (`DisplayName.cs`) |
| **Email** (opcjonalny) | niepuste, max długość, regex | **250**, `^[^@\s]+@[^@\s]+\.[^@\s]+$` | `TranslatorEntity.Email.InvalidFormat` / `.LongerThanAllowed` (`Email.cs`) |

---

## 5. PrecomputedTranslationFile — projekcja dystrybucyjna

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-5.1 | 🟢 ⚠️ To `Entity`, **nie** `AggregateRoot` | Nie broni żadnej reguły; jest wyliczany i można go odbudować; typ niezmienny. Zapisuje go `IPrecomputedTranslationFileStore` (Store ≠ Repository; ADR-0007). | `PrecomputedTranslationFile.cs` |
| INV-5.2 | 🟢 Jeden wiersz na język (klucz naturalny) | Store aktualizuje po `Language`: `TryRefreshAsync` = jeden `UPDATE` w miejscu, bez ładowania starej, wielomegabajtowej treści (PERF-04); `Insert` tylko dla pierwszego wiersza języka. | `PrecomputedTranslationFile.cs`, `IPrecomputedTranslationFileStore.cs` |
| INV-5.3 | 🔵 Plik = wiersze **`Approved` i nieusunięte**, sort `FileId` → `GossipId` | Przebudowa po każdym zapisie, który zmienia zbiór dystrybucji (import, upsert na `Approved`, approve, bulk approve) — **w tle, z debounce** (ADR-0021). Handler po commicie daje sygnał `ITranslationFileRebuildScheduler`; worker zbiera sygnały (okno domyślnie 2 s, max 5 min) i robi jedną przebudowę naraz. Pobranie może chwilę „gonić" commit. | `PrecomputedTranslationFileProjector.cs`, `TranslationFileRebuildScheduler.cs`, `TranslationFileRebuildSettings.cs` |
| INV-5.4 | 🔵 Kolumna `approved` zawsze `1`, końce linii CRLF | Zapis zgodny bajt w bajt z writerem patchera (golden fixture + round trip). ETag = hex SHA-256 treści i jest też hashem integralności po stronie patchera (AUDIT-SEC-01): algorytm i format zmienia się tylko razem z patcherem. | `TranslationFileSerializer.cs` |

---

## 6. Serwis domenowy — TranslationDiffService

Czysty diff (`static class`, `ComputePlanAsync`) uploadu z zapisanym stanem, po tożsamości fragmentu.
Pracuje na wierszach wartości (spec 0006): mapa klucz → `SourceHash` kontra strumień
`StoredSourceDigest`, porównanie hash do hasha, bez tekstów źródeł i bez agregatów. **Nie dotyka
bazy** — handler wykonuje plan, gdy przejdzie guard truncacji.

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-6.1 | 🟢 Pięć rozłącznych wyników na wiersz | **Added** (para nieznana) · **Source-changed** (inny hash) · **Removed** (zapisana para nieobecna) · **Restored** (usunięta para wraca z identycznym źródłem) · **Unchanged**. | `TranslationDiffService.cs` |
| INV-6.2 | 🟢 Unieważnienie liczy się tylko, gdy wiersz **miał polski** | `HasPolish` = `Draft`/`Approved`/`NeedsReview`. | `TranslationDiffService.cs` |
| INV-6.3 | 🟢 `RemovedFraction` = 0 przy pierwszym imporcie | Brak aktywnych wierszy ⇒ mianownik 0 ⇒ ułamek 0; guard truncacji nie zadziała na pierwszym wczytaniu. | `TranslationDiffPlan.cs` |
| INV-6.4 | 🟢 `Removed` liczy tylko aktywne, nieobecne pary | `!stored.IsRemoved` i klucza nie ma w uploadzie. | `TranslationDiffService.cs` |
| INV-6.5 | 🟢 ⚠️ Echo naszego polskiego to nie zmiana źródła (spec 0012) | Gdy przychodzący hash różni się od źródła, ale równa się `StoredSourceDigest.EchoHash` (obecny polski wiersza), wiersz liczy się jako `Unchanged` (albo `Restored`, jeśli był usunięty). `EchoedCount` służy tylko do raportu. | `TranslationDiffService.cs`, `TranslationDiffPlan.cs` |

---

## 7. Aplikacja — import

`POST /api/v1/game-versions/{id}/import` (admin). Walidator komendy + handler; dwa przebiegi po
strumieniu (spec 0006): pierwszy waliduje i liczy diff bez zapisu, drugi wykonuje plan w **jednej
atomowej transakcji** (COPY dla dodanych, zmiany w porcjach dla reszty — ADR-0011; wszystko albo
nic, ponowny upload jest idempotentny). Błędy importu to `DataConflict` → **422**.

| # | Invariant | Reguła | Błąd |
|---|-----------|--------|------|
| INV-7.1 | 🔵 `GameVersionId` niepusty, `FileStream` nie-null (i z możliwością przewijania) | Walidator komendy. Endpoint daje strumień, który można przewinąć (bufor multipart; kopia do pliku tymczasowego jako zapas). | `ImportExportedTexts.cs` |
| INV-7.2 | 🔵 Nieznana wersja → `NotFound` (404) | Odczyt z repozytorium przed parsowaniem. | `GameVersionEntity.NotFound` (`ImportExportedTexts.cs`) |
| INV-7.3 | 🔵 Plik z błędami parsowania jest odrzucony | Ucięty plik nie może udawać masowego usunięcia; komunikat zbiera do 100 błędów linii. Błędem parsowania jest też niepoprawny UTF-8 i `source_digest`, który nie pasuje do wiersza (ADR-0047). | `Import.ParseFailed` (`ImportExportedTexts.cs`, `ImportErrors.cs`, `TranslationExportParser.cs`) |
| INV-7.4 | 🔵 Pusty upload jest odrzucony | Plik bez wierszy nie oznaczy wersji jako przetworzonej. | `Import.EmptyUpload` (`ImportErrors.cs`) |
| INV-7.5 | 🔵 Błędny wiersz albo powtórzony klucz odrzuca cały import | Walidacja VO dla każdego wiersza + sprawdzenie unikalności klucza przy budowie mapy klucz → hash. | `Import.InvalidRow` / `Import.DuplicateFragmentKey` (`ImportExportedTexts.cs`) |
| INV-7.6 | 🔵 ⚠️ Usunięcie > **20%** aktywnych jest blokowane bez override | `RemovedFraction > MaxRemovedFractionWithoutOverride` (domyślnie `0.20`) bez `allowMassRemoval=true` → błąd, na pełnym planie, przed jakimkolwiek zapisem. | `Import.MassRemovalBlocked` (`ImportExportedTexts.cs`, `ImportSettings.cs`) |
| INV-7.7 | 🔵 Status `Processed` zmienia się w **tej samej** transakcji co diff | Przejście jest sprawdzane przed zapisem (superseded → 422 bez zapisu), a zmiana statusu commituje się z ostatnim zapisem transakcji. Potem **planowana** jest przebudowa artefaktu (debounce, ADR-0021). | `ImportExportedTexts.cs` |
| INV-7.8 | 🔵 Przetworzenie wersji **zastępuje** starsze `Unprocessed` | W tej samej transakcji każda wcześniej zarejestrowana, wciąż nieprzetworzona wersja dostaje `MarkSuperseded` — od tej chwili import do niej jest odrzucany (INV-3.2). Lista trafia do `warnings`. | `ImportExportedTexts.cs` |
| INV-7.9 | 🔵 Upload ma limit rozmiaru | `RequestSizeLimit` = `Import:MaxUploadBytes` (domyślnie **256 MB**, `ImportUploadLimits.MaxUploadBytes`); większy plik → **413**. | `ImportExportedTexts.cs`, `ImportSettings.cs`, `ImportUploadLimits.cs` |

---

## 8. Aplikacja — translations (upsert / approve / odczyty)

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-8.1 | 🔵 Upsert: `FileId > 0`, `GossipId >= 0`, `TranslatedText` niepuste i ≤ **32767** znaków | Walidator komendy; limit to `DatFormatConstants.MaxTranslatedTextLength`, granica formatu DAT (ADR-0043). Za nim stoją guard domenowy (INV-1.7) i `CHECK` w bazie. | `UpsertTranslation.cs`, `TranslationConfiguration.cs` |
| INV-8.2 | 🔵 Upsert nieistniejącego fragmentu → `NotFound` (404) | Wiersz powstaje z importu, nie z upsertu. | `TranslationEntity.NotFound(fileId, gossipId)` (`UpsertTranslation.cs`) |
| INV-8.3 | 🔵 Upsert na miękko usuniętym → 422 | Usunięty wiersz jest poza pracą. | `TranslationEntity.CannotEditRemoved` (`UpsertTranslation.cs`) |
| INV-8.4 | 🔵 Upsert tworzy profil autora **przed** stemplowaniem FK | Prowizjonowanie w handlerze (middleware z INV-4.4 nie daje gwarancji). | `UpsertTranslation.cs` |
| INV-8.5 | 🔵 Edycja `Approved` planuje przebudowę artefaktu | `wasApproved` ⇒ `Schedule` po commicie (wiersz wypadł ze zbioru dystrybucji); przebudowa w tle (ADR-0021). | `UpsertTranslation.cs` |
| INV-8.6 | 🔵 Approve: `Id ≠ Empty` (walidator); nieznany → 404; tworzy profil recenzenta; po sukcesie planuje przebudowę | Po `Approve` wiersz wraca do zbioru dystrybucji, więc artefakt jest przebudowywany w tle. | `ApproveTranslation.cs` |
| INV-8.7 | 🔵 Lista: nieobsługiwany `lang` → 400 (walidacja w handlerze); lista jest **anonimowa** (tylko odczyt, #309) | Dziś obsługiwany jest tylko `polish`. Anonimowy caller dostaje pozycje bez linków HATEOAS. | `Translations.UnsupportedLanguage` (`ListTranslations.cs`) |
| INV-8.8 | 🔵 Lista **pomija** miękko usunięte; domyślny sort `(FileId, GossipId)` + opcjonalny `?sort=` z białej listy; `pageSize` w zakresie 1–100 | `RemovedInVersion == null`; `Page` ≥ 1, `PageSize` przycięty do 1–100. `sort` (`fileId`/`gossipId`/`status`/`submittedAt`, `:asc`/`:desc`) zawsze dostaje unikalny klucz na końcu, więc porządek jest pełny; nieznany klucz daje sort po `FileId`. | `ListTranslations.cs` |
| INV-8.9 | 🔵 ⚠️ Wyszukiwanie escapuje znaki specjalne LIKE | Dosłowne `%`/`_` w tekście LOTRO pasują tylko do siebie (ILIKE, escape `\`). | `ListTranslations.cs` |
| INV-8.10 | 🔵 Get-one: id `Empty` → `NotFound` (bez zapytania do bazy) | Same zera nigdy nie wskazują wiersza. | `GetTranslation.cs` |
| INV-8.11 | 🔵 Stats liczy aktywny katalog; `Translated` = Draft + Approved + NeedsReview | Jedno grupowane zapytanie; `Remaining = Total - Approved`; wynik w cache serwera na **30 s** (`HybridCache`, AUDIT-EF-04). | `GetTranslationStats.cs` |
| INV-8.12 | 🔵 Bulk approve (#322): 1–100 id, duplikaty usuwane; zatwierdza, co się da | Zatwierdzane są tylko wiersze nadal `Draft`/`NeedsReview` (guard domenowy `Approve` decyduje — np. wiersz usunięty w międzyczasie jest pomijany). Reszta liczy się jako skipped, **bez** 404/422 dla pojedynczego wiersza; `Approved + Skipped == Requested`. Jeden `SaveChanges` i jedna zaplanowana przebudowa, tylko gdy coś zatwierdzono. Profil recenzenta powstaje przed partią — błąd przerywa całość. | `BulkApproveTranslations.cs` |
| INV-8.13 | 🔵 Publiczny podgląd postępu (`GET /progress`, #309) | Anonimowy; te same grupy co stats + najnowsza wersja **Processed** (`null` przed pierwszym importem); tylko sumy; cache serwera 30 s. | `GetPublicProgress.cs` |

---

## 9. Aplikacja — game versions

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-9.1 | 🔵 Register: `Version` niepuste, ≤ **12** znaków | Walidator komendy. | `RegisterGameVersion.cs` |
| INV-9.2 | 🔵 Powtórzona wersja → 422 (po formie kanonicznej) | `ExistsByVersionAsync(version)` po `LotroNotationVersion.Create`, czyli po kanonikalizacji. | `GameVersionEntity.LotroNotationVersion.AlreadyTaken` (`RegisterGameVersion.cs`) |
| INV-9.3 | 🔵 Register zwraca **201** z `Location` na endpoint pozycji | `/api/v1/game-versions/{id}`. | `RegisterGameVersion.cs` |
| INV-9.4 | 🔵 Lista bez stronicowania; domyślny sort `DetectedAt` malejąco + opcjonalny `?sort=` | Wierszy jest mało (jeden na update gry). Klucze `version`/`detectedAt`/`status` (sort po **tekście**, nie po wartości liczbowej); nieznany klucz daje sort po `DetectedAt`. | `ListGameVersions.cs` |
| INV-9.5 | 🔵 Get-one: id `Empty` → `NotFound` | Bez zapytania do bazy. | `GetGameVersion.cs` |
| INV-9.6 | 🔵 Delete (#209, admin): każda wersja **poza** `Processed`, **i** bez referencji | Guard domenowy `EnsureCanBeDeleted` (INV-3.7) + sprawdzenie `AnyReferencesGameVersionAsync` w handlerze → 422; nieznany id → 404; sukces → **204**. Admin dostaje link `delete` na wersji innej niż `Processed` (#624). Duplikat przy `POST` nadal ignoruje status: błędną rejestrację naprawia się usunięciem wiersza, nie drugim wierszem z tym samym numerem. | `GameVersionEntity.CannotDeleteReferencedVersion` (`DeleteGameVersion.cs`) |

---

## 10. Aplikacja — dystrybucja pliku

`GET /api/v1/translation-files/{lang}` — **anonimowy** (pobiera go CLI i aplikacja gracza).

| # | Invariant | Reguła | Błąd / uwaga |
|---|-----------|--------|--------------|
| INV-10.1 | 🔵 `lang` musi być `polish` | Inaczej walidacja w handlerze. | `TranslationFiles.UnsupportedLanguage` (400) (`GetTranslationFile.cs`) |
| INV-10.2 | 🔵 Brak zbudowanego artefaktu → 404 | Plik nigdy nie jest budowany na żądanie (przebudowa w tle — INV-5.3). Pełne pobranie strumieniuje kopię z dysku API, zapisaną raz na hash treści (ADR-0064). | `TranslationFiles.NotFound` (`GetTranslationFile.cs`) |
| INV-10.3 | 🔵 ETag = hash treści; `If-None-Match` → **304** | Silny tag; `*` albo dopasowanie ⇒ 304. Decyzja wymaga tylko hasha, bez czytania wielomegabajtowej treści (PERF-01, #286). `Cache-Control: private, no-cache`. | `GetTranslationFile.cs` |
| INV-10.4 | 🔵 ⚠️ ETag = hash integralności (AUDIT-SEC-01, #391) | Hex SHA-256 treści w UTF-8; patcher liczy hash pobranego pliku i odrzuca go przy niezgodności. Algorytm i format silnego ETagu to kontrakt między kontekstami — zmiana tylko razem z patcherem. | `GetTranslationFile.cs` |

---

## 11. AuthSystem — uwierzytelnianie

Własny serwer OpenIddict + ASP.NET Identity. Opis całości: [auth-tutorial.md](auth-tutorial.md).

| # | Invariant | Reguła | Lokalizacja |
|---|-----------|--------|-------------|
| INV-11.1 | 🔵 Hasło: **8–128** znaków, co najmniej jedna cyfra, mała litera, wielka litera i znak specjalny; nie może być z wycieku | FluentValidation w handlerach API; Identity `RequiredLength=8` + `PasswordMaxLengthValidator` (≤128), więc limit działa też na stronie resetu hasła, która woła Identity bezpośrednio (#1046). Obie warstwy czytają `PasswordConstants`. `BreachedPasswordValidator` odrzuca hasło znane z wycieków przy każdym ustawianiu hasła; gdy sprawdzenie nie może się wykonać, hasło przechodzi (ADR-0066). | `PasswordConstants.cs`, `PasswordValidationRules.cs`, `PasswordMaxLengthValidator.cs`, `BreachedPasswordValidator.cs`, `PersistenceDependencyInjection.cs` |
| INV-11.2 | 🔵 **E-mail jest loginem** (ADR-0022): unikalny bez względu na wielkość liter, ≤ 250, regex; Username = **nazwa tylko do wyświetlania**: unikalna (bez względu na wielkość liter), `\A[a-zA-Z0-9]+\z`, ≤ 150 | Walidator rejestracji + `UsernameConstants`/`EmailConstants` + Identity `AllowedUserNameCharacters`. Unikalność e-maila trzyma **unikalny `EmailIndex`** na `NormalizedEmail`. | `RegisterUser.cs`, `UsernameConstants.cs`, `EmailConstants.cs`, `PersistenceDependencyInjection.cs`, `ApplicationUserConfiguration.cs` |
| INV-11.3 | 🔵 Akceptacja polityki prywatności i **regulaminu** (LEGAL-03) **musi być true**; zgody na przetwarzanie danych **nie ma** (#1073) | Inaczej walidacja rejestracji odrzuca. Obie akceptacje i ich daty są zapisywane i widoczne w eksporcie danych. Konto działa na podstawie umowy (art. 6 ust. 1 lit. b RODO), więc rejestracja nie pyta o zgodę (ADR-0068). Kolumny `DataProcessingConsentGiven` i `DataProcessingConsentDate` zostają tylko dla starszych kont, do usunięcia w kroku expand/contract (ADR-0023, #1130). | `RegisterUser.cs` |
| INV-11.4 | 🔵 ⚠️ Nowy użytkownik dostaje rolę **`Translator`** | Rejestracja → `Translator`; seedowany admin → `Admin`. | `RegisterUser.cs`, `DatabaseSeederExtensions.cs` |
| INV-11.5 | 🔵 Potwierdzony e-mail **wymagany do logowania**; lockout **5 prób / 5 min** | `SignIn.RequireConfirmedEmail`, `Lockout.MaxFailedAccessAttempts=5`. Konto z zaplanowanym usunięciem nie liczy błędnych haseł — blokadę trzyma okno usunięcia (INV-11.9, #861). Zgadywanie hasła takiego konta hamuje osobny budżet na konto: **10 prób / 15 min**; po jego wyczerpaniu nawet poprawne hasło dostaje ogólny komunikat (#881, aneks ADR-0053). | `PersistenceDependencyInjection.cs`, `Login.cshtml.cs`, `AccountBudgets.cs` |
| INV-11.6 | 🔵 Tokeny: access **5 min** (ADR-0049), refresh **9 h** od ostatniego użycia (referencyjne, rotowane, przesuwane); link e-mail / reset **24 h** | Refresh tokeny są w bazie, więc można je unieważnić; każde użycie daje nowy token z pełnymi 9 h. 9 h = 8 h bezczynności cookie strony, które niesie token, plus zapas (#1014). | `OpenIddictSettings.cs`, `OpenIddictExtensions.cs`, `AuthenticationDependencyInjectionExtensions.cs` (Frontend), `PersistenceDependencyInjection.cs` |
| INV-11.7 | 🔵 Odpowiedź nie zdradza, czy adres ma konto | Forgot/Resend zawsze kończą się sukcesem. Token endpoint i strona logowania liczą hash na niby, gdy nie ma użytkownika (także przy lockoucie, a strona logowania także przy wyczerpanym budżecie konta z zaplanowanym usunięciem — #881). Sprawdzenie potwierdzenia e-maila jest **po** weryfikacji hasła. Anonimowe odpowiedzi, które ukrywają istnienie konta, czekają do minimalnego czasu (`IResponseTimeFloor`, ADR-0059). | `TokenEndpoint.cs`, `Login.cshtml.cs`, `ResponseTimeFloors.cs` |
| INV-11.8 | 🔵 ⚠️ **Brak sagi rejestracji** | Rejestracja tworzy tylko użytkownika Auth; profil `Translator` powstaje leniwie przy pierwszym uwierzytelnionym żądaniu do TMS (ADR-0002 §7, ADR-0004 z aneksem). | `RegisterUser.cs` |
| INV-11.9 | 🔵 `DeleteAccount` **planuje** usunięcie RODO — **14 dni na anulowanie** (ADR-0031 / LEGAL-01) | Wymaga hasła. Nic nie jest kasowane od razu: `DeletionScheduledAt` = teraz, `LockoutEnd` = koniec okna (błędne hasła go nie skracają — #861), nowy security stamp i unieważnienie tokenów OpenIddict. Mail z jednorazowym linkiem anulowania idzie przez outbox, zapisany w tej samej transakcji (ADR-0038). Usunięcie wykonuje finalizer **po** oknie i powiadamia TMS (ADR-0065). Anulowanie zdejmuje lockout i **wymusza reset hasła**; reset hasła w trakcie okna jest zablokowany (konto przywraca tylko link z maila). | `DeleteAccount.cs`, `CancelAccountDeletion.cs`, `AccountDeletionFinalizer.cs`, `ResetPassword.cs`, `GdprSettings.cs` |
| INV-11.10 | 🔵 Auth code + **PKCE**; poza Development/Testing klucze RSA-2048 / AES-256 są sprawdzane przy starcie | Development i Testing używają kluczy jednorazowych; password flow jest włączony tylko w `Testing`. | `OpenIddictExtensions.cs` |
| INV-11.11 | 🔵 **Logowanie = e-mail + hasło** na każdej ścieżce | Strona logowania i password grant używają `FindByEmailAsync`; username **nigdy nie loguje** (username nie może zawierać `@`). Claim `name` = username (ADR-0022). | `Login.cshtml.cs`, `TokenEndpoint.cs` |

---

## 12. Rozbieżności i rzeczy warte uwagi

Krótka lista miejsc oznaczonych ⚠️; szczegóły są w podanym wierszu.

- `PreviousSourceText` jest zamrożony na kolejne zmiany źródła (INV-1.4).
- `GossipId == 0` jest legalny (INV-2.1).
- Sama zmiana argumentów to zmiana źródła (INV-2.2).
- Forma kanoniczna wersji: `48` i `48.0` to ta sama wersja, więc kolidują przy rejestracji (INV-3.5, INV-9.2).
- Porządek wersji to kolejność rejestracji, nie numer (INV-3.4) — DAT vnum nie nadaje się na wersję treści (knowledge base).
- Długość i liczba członów wersji są liczone na surowym wejściu (INV-3.6, INV-3.8).
- Import nie widzi echa naszego polskiego jako zmiany (INV-6.5).
- Usunięcie ponad 20% aktywnych wierszy wymaga `allowMassRemoval=true` (INV-7.6, spec 0001 Q4).
- Przebudowa artefaktu jest asynchroniczna (INV-5.3, ADR-0021); stats i progress mają dodatkowo cache 30 s (INV-8.11, INV-8.13).
- Lista tłumaczeń i `GET /progress` są anonimowe; każda zmiana stanu wymaga logowania (INV-0.6, INV-8.7).
- Bulk approve pomija wiersze, których nie da się zatwierdzić, zamiast zwracać 404/422 (INV-8.12).
- Nowy użytkownik dostaje rolę `Translator`; ról jest tylko dwie: `Admin` i `Translator` (INV-11.4).
- Rejestracja nie tworzy profilu `Translator` (INV-11.8).
