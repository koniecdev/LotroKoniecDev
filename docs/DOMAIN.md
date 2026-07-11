# Dokumentacja domeny — LotroKoniecDev TMS

> Spacer po modelu domenowym **Translation Management System** (TMS), wygenerowany przez analizę
> **kodu źródłowego** (warstwa Domain + Domain Services + read modele + konfiguracje EF), a **nie**
> istniejącej dokumentacji. **Kod jest źródłem prawdy** — przy rozbieżności czytaj kod i popraw ten
> dokument. Lokalizacje podane jako `plik:linia`.
>
> Zakres: tylko kontekst **TMS** (`src/TranslationSystem/**`). Patcher (`src/Patcher/**`) jest
> stabilny (wydany i udowodniony empirycznie; ADR-0002 aneks 2026-06-25 — już nie „zamrożony") i ma
> własny, prostszy model — tutaj go nie opisujemy.

## Spis treści

1. [Przegląd architektury](#przegląd-architektury)
2. [Agregaty](#agregaty)
   - [1. Translation (rdzeń)](#1-translation--rdzeń-domeny)
   - [2. GameVersion](#2-gameversion)
   - [3. Translator](#3-translator-adr-0004)
3. [Maszyna stanów Translation — cykl aktualizacji](#maszyna-stanów-translation--cykl-aktualizacji-spec-0001)
4. [Obiekty wartości (Value Objects)](#obiekty-wartości-value-objects)
5. [Serwisy domenowe](#serwisy-domenowe)
6. [Projekcja: PrecomputedTranslationFile](#projekcja-precomputedtranslationfile)
7. [Repozytoria i Unit of Work](#repozytoria-i-unit-of-work)
8. [CQRS — rozdział read/write](#cqrs--rozdział-readwrite-adr-0002)
9. [Obsługa błędów](#obsługa-błędów)
10. [Struktura projektu](#struktura-projektu)
11. [Wzorce projektowe](#wzorce-projektowe)

---

## Przegląd architektury

TMS to jeden z **dwóch bounded contextów** w repo (drugim jest stabilny patcher CLI). Konteksty nie
mają wzajemnych referencji projektowych — łączy je **wyłącznie kontrakt pliku** `||` (format
tłumaczeń). TMS jest podniesiony 1:1 z referencyjnego `TheKittySaver` (Vertical Slice Architecture,
domena DDD, monada `Result`, serwer OpenIddict), z **jednym odstępstwem na całe repo: brak mediatora
(ADR-0001)**.

Kluczowe cechy:

- **Vertical Slice Architecture** — jeden use case = jeden plik w `…API/Features/<Obszar>/<Akcja>.cs`
  (rekord komendy/zapytania + handler + walidator), spięty jawną rejestracją w DI. Brak mediatora:
  handlery implementują własne `ICommandHandler<,>`/`IQueryHandler<,>` z `SharedKernel.Messaging`,
  a konsument wstrzykuje **zamknięty** interfejs handlera (ADR-0001).
- **CQRS, rozdział read/write od pierwszego dnia (ADR-0002).** Komendy ładują i mutują agregaty przez
  repozytoria + `IUnitOfWork`; zapytania czytają płaskie POCO read modele przez `IApplicationReadDbContext`.
  Model zapisu nigdy nie obsługuje list/wyszukiwania.
- **Błędy są wartościami, nie wyjątkami.** Reguły biznesowe → `Result.Failure(Error)` z fabryk
  `DomainErrors.*`. Guardy (`Ensure`, `ArgumentNullException`) są tylko dla błędów programisty.
- **Brak prymitywnej obsesji.** Każdy koncept domenowy niosący ograniczenie lub tożsamość jest
  `ValueObject` albo strongly-typed ID — nigdy surowym `string`/`int`/`Guid`.
- **Agregaty są celowo proste.** Nasze agregaty są znacznie mniejsze niż KittySaverowy `Cat` —
  nie nadymamy ich. Brak domain eventów (świadomy non-lift, ADR-0002): rdzeniowa pętla ich nie
  potrzebuje.

Kierunek zależności (warstwy TMS):

```
API  ─▶  Domain  ─▶  Primitives        (Persistence / ReadModels(+EF) / Projections / Contracts wpinają się obok)
        (ReadModels ─▶ Primitives — read modele nie znają Domain; ADR-0007 „read projections are not aggregates")
```

Trzy agregaty (`Translation`, `GameVersion`, `Translator`) plus jedna **materializowana projekcja**
(`PrecomputedTranslationFile`) — gotowy do dystrybucji plik `polish.txt`, świadomie **nie** będący
agregatem (ADR-0007).

---

## Agregaty

### 1. Translation — rdzeń domeny

`src/TranslationSystem/…Domain/Aggregates/TranslationAggregate/Entities/Translation.cs`

Jeden **mutowalny wiersz na fragment tekstu** — nigdy kopia per wersja gry. Niesie angielskie źródło,
opcjonalny polski przekład i wskaźniki wersji, które dają grupowanie per wersja bez duplikowania
wierszy przy każdym patchu (spec 0001).

| Właściwość | Typ | Rola |
|---|---|---|
| `FragmentKey` | `FragmentKey` (VO) | stabilna tożsamość `(FileId, GossipId)` ponad wersjami gry; niezmienna |
| `Source` | `TranslationSource` (VO) | angielskie źródło: tekst + dwie kolumny argumentów, jako jedna wartość |
| `TranslatedText` | `string?` | polski przekład (`null` dopóki nieprzetłumaczony) |
| `PreviousSourceText` | `string?` | zamrożone angielskie źródło, do kontekstu side-by-side po unieważnieniu |
| `SubmittedById` | `TranslatorId?` | kto ostatnio wprowadził polski (FK do `Translators`, **nie** `IdentityId` — ADR-0004) |
| `ApprovedById` | `TranslatorId?` | kto zatwierdził (FK do `Translators`) |
| `Status` | `TranslationStatus` | `Untranslated · Draft · Approved · NeedsReview` |
| `IntroducedInVersion` | `GameVersionId` | wersja, w której fragment się pojawił |
| `LastSourceChangeInVersion` | `GameVersionId?` | wersja ostatniej zmiany źródła |
| `RemovedInVersion` | `GameVersionId?` | wersja miękkiego usunięcia (`null` ⇒ aktywny) |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | znaczniki czasu |
| `IsRemoved` | `bool` (computed) | `RemovedInVersion is not null` (`Translation.cs:33`) |

**Zachowania** (każde stempluje `UpdatedAt`):

- `CreateUntranslated(...)` — fabryka wiersza *dodanego* (baseline lub diff): samo angielskie źródło,
  status `Untranslated`, stempluje `IntroducedInVersion` (`Translation.cs:39`).
- `ApplySourceChange(newSource, changedInVersion, now)` — źródło się zmieniło: nadpisuje angielskie
  źródło; jeśli istniała polska praca w stanie `Draft`/`Approved`, **unieważnia** ją (→ `NeedsReview`,
  zamraża `PreviousSourceText`); czyści miękkie usunięcie; stempluje `LastSourceChangeInVersion`
  (`Translation.cs:66`).
- `MarkRemoved(removedInVersion, now)` — miękkie usunięcie: wykluczony z pracy i z dystrybuowanego
  pliku, nigdy nie kasowany twardo (`Translation.cs:90`).
- `Restore(now)` — ponowne dodanie z identycznym źródłem: czyści usunięcie, poprzedni status (w tym
  `Approved`) zostaje, bo stary polski jest wciąż ważny (`Translation.cs:103`).
- `ProvideTranslation(translatedText, submittedBy, now)` — dołącza/zastępuje polski draft, stempluje
  tłumacza; każdy poprzedni status → `Draft` (edycja `Approved` świadomie wyciąga wiersz z dystrybucji
  do czasu ponownej akceptacji) (`Translation.cs:118`).
- `Approve(approvedBy, now)` — akceptuje do dystrybucji: wymaga polskiej treści i nieusuniętego wiersza,
  ustawia `Approved`, stempluje recenzenta, czyści `PreviousSourceText`; zwraca `Result`
  (`Translation.cs:136`).

### 2. GameVersion

`src/TranslationSystem/…Domain/Aggregates/GameVersionAggregate/Entities/GameVersion.cs`

Wersja gry zarejestrowana ręcznie przez admina (watcher forum — M2-18/#85 — wciąż niezaimplementowany;
rejestracja ręczna pozostaje kanoniczną ścieżką, por. ADR-0030). Identyfikator wersji
treści to `LotroNotationVersion` (VO w postaci kanonicznej). Status: `Unprocessed → Processed`, z boczną
ścieżką `Superseded`.

| Właściwość | Typ | Rola |
|---|---|---|
| `LotroNotationVersion` | `LotroNotationVersion` (VO) | kanoniczna notacja kropkowa, np. `48.0` |
| `DetectedAt` | `DateTimeOffset` | czas wykrycia = porządek wersji (forum jest chronologiczne; **brak parsowania semver**) |
| `Status` | `GameVersionStatus` | `Unprocessed · Processed · Superseded` |

**Zachowania** (zwracają `Result` — bronią przejść stanu):

- `MarkAsProcessed()` — `Superseded` nigdy nie może być przetworzony; w przeciwnym razie → `Processed`
  (re-upload do już przetworzonej wersji jest legalny i idempotentny) (`GameVersion.cs:26`).
- `MarkSuperseded()` — `Processed` nie może być cofnięty do `Superseded` (przetworzona praca nigdy nie
  jest cofana); spiętrzone nieprzetworzone wersje są masowo oznaczane, gdy nowsza zostaje przetworzona
  (`GameVersion.cs:40`).
- `EnsureCanBeDeleted()` — guard kasowania (#209): tylko wersja `Unprocessed` może zostać usunięta;
  przetworzona/superseded jest wpleciona w cykl aktualizacji. Cross-agregatowy warunek „żadne
  tłumaczenie jej nie referuje" zostaje w handlerze `DeleteGameVersion` (`GameVersion.cs:58`).
- `Create(version, detectedAt)` — fabryka; powstaje jako `Unprocessed` (`GameVersion.cs:68`).

### 3. Translator (ADR-0004)

`src/TranslationSystem/…Domain/Aggregates/TranslatorAggregate/Entities/Translator.cs`

Lokalna dla TMS projekcja uwierzytelnionego użytkownika edytującego tłumaczenia — chudy odpowiednik
KittySaverowego `Person`. Trzyma czytelną tożsamość renderowaną w edytorze (`DisplayName`, opcjonalny
`Email`) kluczowaną przez cross-contextowy `IdentityId` (id użytkownika z AuthSystem). **Prowizjonowany
leniwie i idempotentnie** przy **pierwszym uwierzytelnionym żądaniu** (middleware
`TranslatorProvisioningMiddleware`; ADR-0004 z aneksem 2026-06-24 — wcześniej dopiero przy pierwszym
zapisie); handlery zapisu dodatkowo prowizjonują autorytatywnie przed stemplowaniem FK. Rozwiązanie
tożsamość → `TranslatorId` jest cache'owane (L1 `HybridCache`, TTL 5 min, fingerprint claimów —
PERF-07). Szczegóły w [auth-tutorial.md](auth-tutorial.md) i ADR-0004.

| Właściwość | Typ | Rola |
|---|---|---|
| `IdentityId` | `IdentityId` (VO/STID) | jedyna cross-contextowa referencja do Auth; **unikalna** |
| `DisplayName` | `DisplayName` (VO) | nazwa renderowana w edytorze (z claimu `name`) |
| `Email` | `Email?` (VO) | opcjonalny (claim `email` może nie istnieć) |
| `ProvisionedAt` | `DateTimeOffset` | czas prowizjonowania (niezmienny) |

**Zachowania:**

- `RefreshProfile(displayName, email)` — odświeża `DisplayName`/`Email` z bieżących claimów na każdym
  dotknięciu (przemianowane konto zbiega się bez osobnej synchronizacji); **nie** stempluje znacznika czasu
  (`Translator.cs:31`).
- `Create(identityId, displayName, email, now)` — fabryka, stempluje niezmienny `ProvisionedAt` (`Translator.cs:39`).

**Łańcuch referencji:** `Translation.SubmittedById/ApprovedById → Translator.Id (TranslatorId)`, a
`Translator.IdentityId → użytkownik Auth`. Znormalizowany wiersz `Translator` zamiast denormalizowanej
nazwy na każdym `Translation` — przy zmianie nazwy nic nie dryfuje (ADR-0004).

---

## Maszyna stanów Translation — cykl aktualizacji (spec 0001)

To najciekawsza część domeny. **Pojedynczy enum `TranslationStatus`** (bez równoległej flagi
unieważnienia) sprawia, że nielegalne kombinacje — np. „Approved i jednocześnie unieważniony" — są
**niereprezentowalne** (`TranslationStatus.cs`). Unieważnienie *jest* przejściem do `NeedsReview`.

```
                    CreateUntranslated (import: dodany wiersz)
                              │
                              ▼
                      ┌──────────────┐
                      │ Untranslated │
                      └──────┬───────┘
            ProvideTranslation (upsert)
                              ▼
   ┌──────────────────────▶ Draft ──────── Approve ────────▶ Approved
   │                          ▲                                  │
   │              ProvideTranslation (upsert)        ApplySourceChange (import:
   │                          │                       zmiana źródła nad pracą polską)
   │                          │                                  │
   └── ProvideTranslation ── NeedsReview ◀───────────────────────┘
        (re-translacja)        ▲   (unieważnienie: zamrożenie PreviousSourceText)
                               └── ApplySourceChange (kolejne zmiany źródła nadpisują
                                    Source, ale PreviousSourceText zostaje zamrożony)
```

- **Unieważnienie** = `Draft`/`Approved` → `NeedsReview` w `ApplySourceChange`. `PreviousSourceText`
  zamraża angielskie źródło, względem którego napisano *wciąż obecny* polski — nie jest nadpisywane
  przez kolejne pośrednie zmiany źródła (`Translation.cs:74-84`).
- **Approve** = `Draft`/`NeedsReview` → `Approved`; czyści `PreviousSourceText`, wciąga wiersz z
  powrotem do zbioru dystrybucji.
- **Upsert** na `NeedsReview` → `Draft` (`PreviousSourceText` zachowany do akceptacji).
- **„Fallback do angielskiego":** żaden angielski tekst nie jest nigdy zapisywany w polu polskim;
  unieważniony wiersz jest *wykluczany* z dystrybuowanego pliku, więc `patch` nigdy nie nakłada
  nieaktualnego polskiego (spec 0001).

`GameVersionStatus` ma analogiczną własność: jeden enum `Unprocessed · Processed · Superseded` zamiast
boola + flagi (`GameVersionStatus.cs`).

---

## Obiekty wartości (Value Objects)

Wszystkie dziedziczą po `SharedKernel.BuildingBlocks.ValueObject` (równość po `GetAtomicValues()`),
walidują przez `Result` w `Create(...)` i są niemutowalne.

| VO | Plik | Reguła |
|---|---|---|
| `FragmentKey` | `…TranslationAggregate/ValueObjects/FragmentKey.cs` | `FileId > 0` i `GossipId >= 0`; tożsamość `(FileId, GossipId)`, `FileId` adresuje subplik DAT, `GossipId` (8-bajtowy) fragment |
| `TranslationSource` | `…TranslationAggregate/ValueObjects/TranslationSource.cs` | `Text` + `ArgsOrder` + `ArgsId` jako **jedna** wartość (zmiana struktury placeholderów = zmiana znaczenia); blank/`NULL` w kolumnach argumentów normalizowane do `null` |
| `LotroNotationVersion` | `…GameVersionAggregate/ValueObjects/LotroNotationVersion.cs` | gramatyka `digits(.digits)*`, max 12 znaków; **forma kanoniczna** — segmenty końcowych zer są zwijane, więc `48` = `48.0` = `48.0.0` (ADR-0003) |
| `DisplayName` | `…TranslatorAggregate/ValueObjects/DisplayName.cs` | niepuste, ≤ **150** znaków (trim) |
| `Email` | `…TranslatorAggregate/ValueObjects/Email.cs` | niepuste, ≤ **250**, regex `^[^@\s]+@[^@\s]+\.[^@\s]+$`; opcjonalny na `Translator` |

Strongly-typed ID-y (`TranslationId`, `GameVersionId`, `TranslatorId`, `PrecomputedTranslationFileId`,
oraz cross-contextowy `IdentityId` z SharedKernel) leżą w `TranslationSystem.Primitives` i są generowane
atrybutem `[StronglyTypedId(...)]`. ID-y używają **GUID v7** (`Guid.CreateVersion7()`, czasowo
uporządkowane) i serializują się do JSON jako **goły string GUID**.

---

## Serwisy domenowe

### TranslationDiffService

`src/TranslationSystem/…Domain/Aggregates/TranslationAggregate/Services/TranslationDiffService.cs`

Czysty diff (`static class`, `ComputePlanAsync`) wgranego `exported.txt` względem zapisanego stanu
źródeł, kluczowany po tożsamości fragmentu po całym pliku — od spec 0006 **strumieniowo i na wierszach
wartości**: upload przychodzi jako mapa klucz→hash (`FragmentKeyValue`→`SourceHash`), katalog płynie
jako strumień `StoredSourceDigest`, a źródła porównują się hash-do-hasha, więc serwis nie trzyma ani
stringów źródeł, ani agregatów. Produkuje `TranslationDiffPlan` **bez dotykania bazy** — handler
importu realizuje plan w swojej transakcji po przejściu guarda truncacji (`TranslationDiffService.cs:22`).
Pięć wyników:

| Wynik | Warunek | Akcja |
|---|---|---|
| **Added** | para nieznana | `CreateUntranslated` (samo źródło) |
| **Source-changed** | para znana, źródło różne | `ApplySourceChange` (unieważnia polski, jeśli był) |
| **Removed** | zapisana para nieobecna w uploadzie | `MarkRemoved` (miękko) |
| **Restored** | wcześniej usunięta para wraca z identycznym źródłem | `Restore` |
| **Unchanged** | para znana, źródło identyczne | no-op |

`TranslationDiffPlan` (`…/Services/TranslationDiffPlan.cs`) liczy też `RemovedFraction` —
ułamek aktywnych wierszy, które upload by usunął; **0 dla baseline importu** (brak aktywnych wierszy),
więc guard truncacji nigdy nie tripuje na pierwszym wczytaniu (`TranslationDiffPlan.cs:44-45`). `HasPolish`
(`Draft`/`Approved`/`NeedsReview`) decyduje, czy zmiana źródła liczy się jako unieważnienie
(`TranslationDiffService.cs:85-86`).

---

## Projekcja: PrecomputedTranslationFile

`src/TranslationSystem/LotroKoniecDev.TranslationSystem.Projections/PrecomputedTranslationFile.cs`

Gotowy do dystrybucji plik `polish.txt` (Approved + nieunieważnione + nieusunięte wiersze, posortowane
po `FileId` potem `GossipId`), serwowany przez `GET /api/v1/translation-files/{lang}` z content-hash
ETag (hex SHA-256 — pełni też rolę hasha integralności dla patchera, AUDIT-SEC-01) i regenerowany po
każdym zapisie zmieniającym zbiór dystrybucji.

**Świadomie NIE jest agregatem (ADR-0007 „read projections are not aggregates").** To `Entity`, nie
`AggregateRoot` — nie broni żadnego invariantu, jest wyprowadzony i regenerowalny. Stąd:

- żyje w osobnym projekcie `TranslationSystem.Projections` (Domain nie referuje Projections); typ jest
  niemutowalny — istnieje, by wstawić pierwszy wiersz per język;
- persystowany przez dedykowany port `IPrecomputedTranslationFileStore` (`TryRefreshAsync` —
  set-based `UPDATE` w miejscu, nigdy nie materializuje poprzedniego multi-MB contentu (PERF-04) —
  + `Insert`), **nie** `IRepository` — repozytoria pozostają tylko-agregatowe; implementacja w
  `…Persistence/Projections/PrecomputedTranslationFileStore.cs`;
- przebudowywany przez `IPrecomputedTranslationFileProjector` **w tle, z debounce** (PERF-04,
  ADR-0021): handler zapisu tylko sygnalizuje `ITranslationFileRebuildScheduler.Schedule(...)` po
  commicie, a hostowany worker koalescuje sygnały (okno domyślnie 2 s) i odpala jedną, single-flight
  przebudowę — odpowiedź nie czeka, więc pobranie może chwilowo „gonić" commit.

Strona odczytu (`PrecomputedTranslationFileReadModel`) i typ zapisu **dual-mapują na tę samą fizyczną
tabelę** `TranslationArtifacts` (nazwa fizyczna zachowana po refaktorze typu — ADR-0007).

---

## Repozytoria i Unit of Work

`src/TranslationSystem/…Persistence/`

- `IRepository<TAggregateRoot, TId>` jest **tylko-agregatowy** (`where TAggregateRoot : AggregateRoot<TId>`).
  Konkretnie: `ITranslationRepository`, `IGameVersionRepository`, `ITranslatorRepository`
  (interfejsy w Domain, implementacje w `…Persistence/DomainRepositories/` nad `GenericRepository`).
- `IUnitOfWork` (`…Persistence/DbContexts/Abstractions/IUnitOfWork.cs`) = punkt commitu;
  implementuje go `ApplicationWriteDbContext`.
- `IPrecomputedTranslationFileStore` — osobny **Store** (nie repozytorium) dla projekcji
  (interfejs w `…Projections/IPrecomputedTranslationFileStore.cs`, implementacja w
  `…Persistence/Projections/PrecomputedTranslationFileStore.cs`). Współistnienie `Store` i
  `Repository` jest zamierzone (ADR-0007), nie niespójnością.

---

## CQRS — rozdział read/write (ADR-0002)

| | Zapis | Odczyt |
|---|---|---|
| Kontekst | `ApplicationWriteDbContext` (= `IUnitOfWork`, posiada migracje) | `ApplicationReadDbContext` za `IApplicationReadDbContext` |
| Model | agregaty domenowe (mutowalne) | płaskie POCO read modele (`IReadOnlyEntity<TId>`) |
| Kto | command handlery (repozytoria + UoW) | query handlery (LINQ po read modelach) |

Read modele (`TranslationSystem.ReadModels`) i ich konfiguracje EF (`…ReadModels.EntityFramework`)
**dual-mapują na te same tabele** co model zapisu — to nie osobne tabele, tylko osobny widok typu na
tym samym wierszu (`ToTable("Translations")` po obu stronach). `TranslationReadModel` dołącza
`SubmittedBy`/`ApprovedBy` (`TranslatorReadModel`) dla rozwiązania nazw wyświetlanych (ADR-0004).
Query handler **nigdy** nie dotyka modelu zapisu.

Tabele (PostgreSQL, schema `translation`): `GameVersions`, `Translations`, `Translators`,
`TranslationArtifacts`.

---

## Obsługa błędów

- **Monady** `Result` / `Result<T>` / `Maybe<T>` (`SharedKernel.Monads`). Reguła biznesowa, która może
  zawieść, zwraca `Result.Failure(Error)`; brak wartości to `Maybe.None`.
- **`Error`** = `(Code, Message, TypeOfError)`. Fabryki w `DomainErrors.*`
  (`…Domain/Core/Errors/*`): `BaseDomainErrors` (prywatne helpery: `Required`, `TooManyCharacters`,
  `AlreadyHasBeenTaken`, `InvalidOperation`, `HasNotBeenFound`) + per-agregat
  (`TranslationAggregateDomainErrors`, `GameVersionAggregateDomainErrors`, `TranslatorAggregateDomainErrors`).
- **Mapowanie na HTTP** (`…API/Extensions/ErrorExtensions.cs`): `Validation→400`, `NotFound→404`,
  `Forbidden→403`, `DataConflict→422`, reszta→`500`; jako `ProblemDetails` z `errorCode`. Szczegóły:
  [API.md §Error contract](API.md#5-error-contract-problemdetails).
- **Guardy** (`Ensure.NotEmpty`, `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrWhiteSpace`)
  rzucają wyjątek — są tylko dla błędów programisty, nie biznesowych.

Pełny katalog egzekwowanych reguł z lokalizacjami: [INVARIANTS.md](INVARIANTS.md).

---

## Struktura projektu

```
src/TranslationSystem/
  LotroKoniecDev.TranslationSystem.Primitives                 # strongly-typed ID-y + enumy (zero zależności domenowych)
  LotroKoniecDev.TranslationSystem.Domain                     # agregaty, VO, serwisy domenowe, DomainErrors
  LotroKoniecDev.TranslationSystem.ReadModels                 # płaskie POCO read modele
  LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework # konfiguracje EF read modeli (dual-map)
  LotroKoniecDev.TranslationSystem.Projections                # PrecomputedTranslationFile + IPrecomputedTranslationFileStore
  LotroKoniecDev.TranslationSystem.Persistence                # Write/Read DbContext, repozytoria, konfiguracje EF, migracje
  LotroKoniecDev.TranslationSystem.Contracts                  # DTO request/response (referowane przez Frontend)
  LotroKoniecDev.TranslationSystem.API                        # endpointy VSA (Features/<Obszar>/<Akcja>.cs), Auth, HATEOAS, parsing
```

## Wzorce projektowe

- **Vertical Slice Architecture** — kohezja per feature zamiast warstw techniczych.
- **Slim SRP handlers bez mediatora (ADR-0001)** — rekord + handler + walidator, jawna rejestracja
  zamkniętego interfejsu w `…API/ApiDependencyInjection.cs`.
- **CQRS read/write split (ADR-0002).**
- **Value Object + strongly-typed ID** — brak prymitywnej obsesji.
- **Result/Maybe monad** — błędy jako wartości.
- **Materialized read projection (ADR-0007)** — projekcja ≠ agregat; `Store` ≠ `Repository`.
- **Debounced background rebuild (ADR-0021)** — zapis sygnalizuje, hostowany worker koalescuje i
  przebudowuje artefakt w tle.
- **Lazy idempotent provisioning (ADR-0004, aneks 2026-06-24)** — tożsamość tłumacza tworzona przy
  pierwszym uwierzytelnionym żądaniu (middleware), z cache'owanym rozwiązaniem tożsamości.
